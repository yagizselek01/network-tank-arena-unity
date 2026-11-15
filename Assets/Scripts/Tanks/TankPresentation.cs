using UnityEngine;

namespace NetworkTankArena.Tanks
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(TankSimulation))]
    public sealed class TankPresentation : MonoBehaviour
    {
        [SerializeField] private Transform visualRoot;
        [SerializeField] private Transform chassisTilt;
        [SerializeField] private Transform turretPivot;
        [SerializeField, Min(0f)] private float bobAmplitude = 0.08f;
        [SerializeField, Min(0f)] private float bobFrequency = 1.5f;
        [SerializeField, Min(0f)] private float bankAngle = 6f;
        [SerializeField, Min(0f)] private float pitchAngle = 4f;
        [SerializeField, Min(0f)] private float wobbleAngle = 1f;
        [SerializeField, Min(0f)] private float wobbleFrequency = 3f;
        [SerializeField, Min(0.01f)] private float fullTiltSpeed = 6f;
        [SerializeField, Min(0.01f)] private float fullBankTurnSpeed = 120f;
        [SerializeField, Min(0f)] private float tiltResponse = 8f;

        [SerializeField, Min(0.01f)] private float turretSmoothingSpeed = 18f;

        private TankSimulation tank;
        private Vector3 baseVisualPosition;
        private Quaternion baseChassisRotation;
        private Quaternion baseTurretRotation;
        private Vector3 previousPosition;
        private float previousYaw;
        private float cosmeticTime;
        private float pitch;
        private float roll;
        private bool initialized;

        //local aim override state
        private bool hasLocalAimOverride;
        private float localAimYaw;
        private float smoothedTurretYaw;
        public float DisplayedTurretYaw => hasLocalAimOverride ? localAimYaw : enableRemoteSmoothing ? smoothedTurretYaw : tank.TurretYaw;    
        
        // Remote aim error tracking
        private float lastReplicatedYaw;
        private float lastTargetChangeTime;
        [SerializeField] private float remoteAimErrorDegrees;

        private float peakRemoteAimErrorDegrees;
        private bool waitingForCatchUp;
        [SerializeField] private float lastCatchUpTimeMs;


        // For calculating the angular change in degrees for the current frame
        private float previousDisplayedYaw;
        [SerializeField] private float frameAngularChangeDegrees;

        private float maxFrameAngularChangeDegrees;

        private float totalFrameAngularChange;
        private int measuredFrameCount;

        // For calculating the average frame angular change over time
        private bool isMeasuring;
        private float measurementEndTime;
        [SerializeField] private float averageFrameAngularChangeDegrees;

        // Smooth damp variables for turret yaw smoothing
        [SerializeField, Min(0.01f)] private float turretSmoothTime = 0.1f;
        private float turretSmoothVelocity;

        [Header("Smoothing Options")]
        [SerializeField] private bool useSmoothDamp = false;

        [SerializeField] private bool enableRemoteSmoothing = true;

        private void Awake()
        {
            tank = GetComponent<TankSimulation>();
            if (visualRoot == null || chassisTilt == null || turretPivot == null ||
                visualRoot.parent != transform || chassisTilt.parent != visualRoot ||
                turretPivot.parent != visualRoot || chassisTilt == turretPivot)
            {
                Debug.LogError("TankPresentation needs VisualRoot directly beneath the tank root, with " +
                    "distinct ChassisTilt and TurretPivot children directly beneath VisualRoot.", this);
                enabled = false;
                return;
            }

            baseVisualPosition = visualRoot.localPosition;
            baseChassisRotation = chassisTilt.localRotation;
            baseTurretRotation = turretPivot.localRotation;
            initialized = true;
        }

        private void OnEnable()
        {
            previousPosition = transform.position;
            previousYaw = transform.eulerAngles.y;
            cosmeticTime = pitch = roll = 0f;
            smoothedTurretYaw = tank.TurretYaw; 
            lastReplicatedYaw = tank.TurretYaw;
            lastTargetChangeTime = Time.unscaledTime;
            waitingForCatchUp = false;
            lastCatchUpTimeMs = 0f;
            peakRemoteAimErrorDegrees = 0f;
            previousDisplayedYaw = tank.TurretYaw;
            totalFrameAngularChange = 0f;
            measuredFrameCount = 0;
            averageFrameAngularChangeDegrees = 0f;
            maxFrameAngularChangeDegrees = 0f;
            turretSmoothVelocity = 0f;
        }

        private void LateUpdate()
        {
            if (!initialized || Time.deltaTime <= 0f)
                return;

            float deltaTime = Time.deltaTime;
            cosmeticTime += deltaTime;
            Vector3 velocity = (transform.position - previousPosition) / deltaTime;
            float forwardSpeed = Vector3.Dot(velocity, transform.forward);
            float yaw = transform.eulerAngles.y;
            float turnRate = Mathf.DeltaAngle(previousYaw, yaw) / deltaTime;
            previousPosition = transform.position;
            previousYaw = yaw;

            float motion = Mathf.Clamp01(velocity.magnitude / fullTiltSpeed);
            float response = 1f - Mathf.Exp(-tiltResponse * deltaTime);
            pitch = Mathf.Lerp(pitch, -Mathf.Clamp(forwardSpeed / fullTiltSpeed, -1f, 1f) * pitchAngle, response);
            roll = Mathf.Lerp(roll, -Mathf.Clamp(turnRate / fullBankTurnSpeed, -1f, 1f) * bankAngle, response);

            visualRoot.localPosition = baseVisualPosition + Vector3.up *
                (Mathf.Sin(cosmeticTime * bobFrequency * 2f * Mathf.PI) * bobAmplitude);
            float wobble = Mathf.Sin(cosmeticTime * wobbleFrequency * 2f * Mathf.PI) * wobbleAngle * motion;
            chassisTilt.localRotation = baseChassisRotation * Quaternion.Euler(pitch, 0f, roll + wobble);

            bool targetChangedThisFrame = false;

            // Update the smoothed turret yaw based on the tank's current turret yaw and local aim override state.
            if(!hasLocalAimOverride)
            {
                float targetChange = Mathf.Abs(Mathf.DeltaAngle(lastReplicatedYaw, tank.TurretYaw));
                if(targetChange > 0.1f)
                {
                    peakRemoteAimErrorDegrees = 0f; // Reset peak error when target changes

                    lastTargetChangeTime = Time.unscaledTime;
                    lastReplicatedYaw = tank.TurretYaw;
                    waitingForCatchUp = true;
                    targetChangedThisFrame = true;
                }
            }


            if (useSmoothDamp)
            {
                // SmoothDampAngle for turret yaw smoothing
                smoothedTurretYaw = Mathf.SmoothDampAngle(smoothedTurretYaw, tank.TurretYaw, ref turretSmoothVelocity, turretSmoothTime);
            }
            else
            {
                // Exponential smoothing for turret yaw
                float smoothingFactor = 1f - Mathf.Exp(-turretSmoothingSpeed * Time.deltaTime);
                smoothedTurretYaw = Mathf.LerpAngle(smoothedTurretYaw, tank.TurretYaw, smoothingFactor);
            }

            // Determine the yaw to display based on local aim override or smoothed turret yaw.
            float displayedYaw;
            if(hasLocalAimOverride) //has local aim override, use that instead of smoothed turret yaw
            {
                displayedYaw = localAimYaw;
            }
            else //no local aim override, use smoothed turret yaw
            {
                displayedYaw = enableRemoteSmoothing ? smoothedTurretYaw : tank.TurretYaw;
            }
            turretPivot.rotation = Quaternion.Euler(0f, displayedYaw, 0f);

            // Calculate the angular change in degrees for the current frame if there's no local aim override.
            if (!hasLocalAimOverride && isMeasuring)
            {
                frameAngularChangeDegrees = Mathf.Abs(Mathf.DeltaAngle(previousDisplayedYaw, displayedYaw));

                previousDisplayedYaw = displayedYaw;

                maxFrameAngularChangeDegrees = Mathf.Max(maxFrameAngularChangeDegrees, frameAngularChangeDegrees);

                // Update total frame angular change and measured frame count for average calculation.
                totalFrameAngularChange += frameAngularChangeDegrees;
                measuredFrameCount++;
                averageFrameAngularChangeDegrees = measuredFrameCount > 0 ? totalFrameAngularChange / measuredFrameCount : 0f;
            }

            // Update remote aim error tracking if there's no local aim override.
            if(!hasLocalAimOverride)
            {
                remoteAimErrorDegrees = Mathf.Abs(Mathf.DeltaAngle(displayedYaw, tank.TurretYaw));
                if (waitingForCatchUp)
                {
                    // Update the peak remote aim error.
                    peakRemoteAimErrorDegrees = Mathf.Max(peakRemoteAimErrorDegrees,remoteAimErrorDegrees);
                }
                // Check if the tank is waiting for a catch-up and the remote aim error is within the threshold.
                if (waitingForCatchUp && !targetChangedThisFrame && remoteAimErrorDegrees <= 1f)
                {
                    // Calculate the catch-up time in milliseconds.
                    lastCatchUpTimeMs = (Time.unscaledTime - lastTargetChangeTime) * 1000f;
                    waitingForCatchUp = false;

                    // Log the catch-up time.
                    if (!isMeasuring && peakRemoteAimErrorDegrees >= 5f)
                    {
                        Debug.Log(
                            $"[REMOTE AIM] peakError={peakRemoteAimErrorDegrees:F1}°, " +
                            $"catchUp={lastCatchUpTimeMs:F1}ms, " +
                            $"smoothingSpeed={turretSmoothingSpeed:F1}"
                        );
                    }

                    peakRemoteAimErrorDegrees = 0f;
                }
            }

            if (isMeasuring && Time.unscaledTime >= measurementEndTime)
            {
                isMeasuring = false;
                PrintAimMetrics();
            }
        }

        private void OnDisable()
        {
            // Clear the local aim override when the component is disabled.
            ClearLocalAimOverride();
            
            if (!initialized)
                return;

            visualRoot.localPosition = baseVisualPosition;
            chassisTilt.localRotation = baseChassisRotation;
            turretPivot.localRotation = baseTurretRotation;

            isMeasuring = false;
            ResetAimMetrics();
        }

        /// <summary>
        /// Sets the local aim override for the tank.
        /// </summary>
        /// <param name="yaw">The yaw to override the local aim with.</param>
        public void SetLocalAimOverride(float yaw) 
        {
            if(!float.IsFinite(yaw)) return;
            localAimYaw = yaw;
            hasLocalAimOverride = true;
        }

        public void ClearLocalAimOverride() // Clears the local aim override, allowing the tank to use its normal aiming behavior.
        {
            hasLocalAimOverride = false;
        }

        [ContextMenu("Reset Aim Metrics")]
        private void ResetAimMetrics()
        {
            totalFrameAngularChange = 0f;
            measuredFrameCount = 0;
            averageFrameAngularChangeDegrees = 0f;
            maxFrameAngularChangeDegrees = 0f;
            frameAngularChangeDegrees = 0f;
            previousDisplayedYaw = DisplayedTurretYaw;
        }

        [ContextMenu("Print Aim Metrics")]
        private void PrintAimMetrics()
        {
            string mode = !enableRemoteSmoothing ? "Original" : useSmoothDamp ? "SmoothDamp" : "Exponential";

            Debug.Log($"[AIM METRICS] Mode: {mode}, " +
                $"Average Frame Angular Change: {averageFrameAngularChangeDegrees:F2}°, " +
                $"Max Frame Angular Change: {maxFrameAngularChangeDegrees:F2}°, " +
                $"Samples: {measuredFrameCount}");
        }

        [ContextMenu("Start Aim Test")]
        private void StartAimTest()
        {
            ResetAimMetrics();
            isMeasuring = true;
            measurementEndTime = Time.unscaledTime + 10f;
        }
    }
}
