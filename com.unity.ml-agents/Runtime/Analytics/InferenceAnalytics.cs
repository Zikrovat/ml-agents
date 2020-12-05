using System;
using System.Collections.Generic;
using Unity.Barracuda;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Inference;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;
using UnityEngine;
using UnityEngine.Analytics;

#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Analytics;
#endif


namespace Unity.MLAgents.Analytics
{
    internal class InferenceAnalytics
    {
        const string k_VendorKey = "unity.ml-agents";
        const string k_EventName = "ml_agents_inferencemodelset";

        /// <summary>
        /// Whether or not we've registered this particular event yet
        /// </summary>
        static bool s_EventRegistered = false;

        /// <summary>
        /// Hourly limit for this event name
        /// </summary>
        const int k_MaxEventsPerHour = 1000;

        /// <summary>
        /// Maximum number of items in this event.
        /// </summary>
        const int k_MaxNumberOfElements = 1000;

        /// <summary>
        /// Models that we've already sent events for.
        /// </summary>
        private static HashSet<NNModel> s_SentModels;

        static bool EnableAnalytics()
        {
            if (s_EventRegistered)
            {
                return true;
            }

#if UNITY_EDITOR
            AnalyticsResult result = EditorAnalytics.RegisterEventWithLimit(k_EventName, k_MaxEventsPerHour, k_MaxNumberOfElements, k_VendorKey);
#else
            AnalyticsResult result = AnalyticsResult.UnsupportedPlatform;
#endif
            if (result == AnalyticsResult.Ok)
            {
                s_EventRegistered = true;
            }

            if (s_EventRegistered && s_SentModels == null)
            {
                s_SentModels = new HashSet<NNModel>();
            }

            return s_EventRegistered;
        }

        public static bool IsAnalyticsEnabled()
        {
#if UNITY_EDITOR
            return EditorAnalytics.enabled;
#else
            return false;
#endif
        }

        /// <summary>
        /// Send an analytics event for the NNModel when it is set up for inference.
        /// No events will be sent if analytics are disabled, and at most one event
        /// will be sent per model instance.
        /// </summary>
        /// <param name="nnModel">The NNModel being used for inference.</param>
        /// <param name="behaviorName">The BehaviorName of the Agent using the model</param>
        /// <param name="inferenceDevice">Whether inference is being performed on the CPU or GPU</param>
        /// <param name="sensors">List of ISensors for the Agent. Used to generate information about the observation space.</param>
        /// <param name="actionSpec">ActionSpec for the Agent. Used to generate information about the action space.</param>
        /// <returns></returns>
        public static void InferenceModelSet(
            NNModel nnModel,
            string behaviorName,
            InferenceDevice inferenceDevice,
            IList<ISensor> sensors,
            ActionSpec actionSpec
        )
        {
            // The event shouldn't be able to report if this is disabled but if we know we're not going to report
            // Lets early out and not waste time gathering all the data
            if (!IsAnalyticsEnabled())
                return;

            if (!EnableAnalytics())
                return;

            var added = s_SentModels.Add(nnModel);

            if (!added)
            {
                // We previously added this model. Exit so we don't resend.
                return;
            }

            var data = GetEventForModel(nnModel, behaviorName, inferenceDevice, sensors, actionSpec);
            // Note - to debug, use JsonUtility.ToJson on the event.
            //Debug.Log(JsonUtility.ToJson(data, true));
#if UNITY_EDITOR
            // TODO re-enable when we're ready to merge.
            //EditorAnalytics.SendEventWithLimit(k_EventName, data);
#else
            return;
#endif
        }


        /// <summary>
        /// Generate an InferenceEvent for the model.
        /// </summary>
        /// <param name="nnModel"></param>
        /// <param name="behaviorName"></param>
        /// <param name="inferenceDevice"></param>
        /// <param name="sensors"></param>
        /// <param name="actionSpec"></param>
        /// <returns></returns>
        static InferenceEvent GetEventForModel(
            NNModel nnModel,
            string behaviorName,
            InferenceDevice inferenceDevice,
            IList<ISensor> sensors,
            ActionSpec actionSpec
        )
        {
            var barracudaModel = ModelLoader.Load(nnModel);
            var inferenceEvent = new InferenceEvent();
            inferenceEvent.BehaviorName = behaviorName;
            inferenceEvent.BarracudaModelSource = barracudaModel.IrSource;
            inferenceEvent.BarracudaModelVersion = barracudaModel.IrVersion;
            inferenceEvent.BarracudaModelProducer = barracudaModel.ProducerName;
            inferenceEvent.MemorySize = (int)barracudaModel.GetTensorByName(TensorNames.MemorySize)[0];
            inferenceEvent.InferenceDevice = (int)inferenceDevice;

            if (barracudaModel.ProducerName == "Script")
            {
                // .nn files don't have these fields set correctly. Assign some placeholder values.
                inferenceEvent.BarracudaModelSource = "NN";
                inferenceEvent.BarracudaModelProducer = "tf2bc.py";
            }

#if UNITY_2019_3_OR_NEWER
            var barracudaPackageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(Tensor).Assembly);
            inferenceEvent.BarracudaPackageVersion = barracudaPackageInfo.version;
#else
            inferenceEvent.BarracudaPackageVersion = "unknown";
#endif

            inferenceEvent.ActionSpec = EventActionSpec.FromActionSpec(actionSpec);
            inferenceEvent.ObservationSpecs = new List<EventObservationSpec>(sensors.Count);
            foreach (var sensor in sensors)
            {
                inferenceEvent.ObservationSpecs.Add(EventObservationSpec.FromSensor(sensor));
            }

            inferenceEvent.TotalWeightSizeBytes = GetModelWeightSize(barracudaModel);
            inferenceEvent.ModelHash = GetModelHash(barracudaModel);
            return inferenceEvent;
        }

        /// <summary>
        /// Simple implementation of the Fowler–Noll–Vo hash function.
        /// https://en.wikipedia.org/wiki/Fowler%E2%80%93Noll%E2%80%93Vo_hash_function
        /// </summary>
        internal class FNVHash
        {
            const ulong kFNV_prime = 1099511628211;
            const ulong kFNV_offset_basis = 14695981039346656037;
            private const int kMaxBytes = 1024;

            public ulong hash;

            public FNVHash()
            {
                hash = kFNV_offset_basis;
            }

            public void Append(float[] values, int startUnused, int count)
            {
                var bytesToHash = sizeof(float) * count;
                for (var i = 0; i < bytesToHash; i++)
                {
                    var b = Buffer.GetByte(values, i);
                    Update(b);
                }
            }

            public void Append(string value)
            {
                foreach (var c in value)
                {
                    Update((byte)c);
                }
            }

            private void Update(byte b)
            {
                hash *= kFNV_prime;
                hash ^= b;
            }

            public override string ToString()
            {
                return hash.ToString();
            }
        }

        /// <summary>
        /// Wrapper around Hash128 that supports Append(float[], int, int)
        /// </summary>
        struct MLAgentsHash128
        {
            private Hash128 m_Hash;

            public void Append(float[] values, int startUnused, int count)
            {
                if (values == null)
                {
                    return;
                }

                for (var i = 0; i < count; i++)
                {
                    var tempHash = new Hash128();
                    HashUtilities.ComputeHash128(ref values[i], ref tempHash);
                    HashUtilities.AppendHash(ref tempHash, ref m_Hash);
                }
            }

            public void Append(string value)
            {
                var tempHash = Hash128.Compute(value);
                HashUtilities.AppendHash(ref tempHash, ref m_Hash);
            }

            public override string ToString()
            {
                return m_Hash.ToString();
            }
        }

        /// <summary>
        /// Compute the total model weight size in bytes.
        /// This corresponds to the "Total weight size" display in the Barracuda inspector,
        /// and the calculations are the same.
        /// </summary>
        /// <param name="barracudaModel"></param>
        /// <returns></returns>
        static long GetModelWeightSize(Model barracudaModel)
        {
            long totalWeightsSizeInBytes = 0;
            for (var l = 0; l < barracudaModel.layers.Count; ++l)
            {
                for (var d = 0; d < barracudaModel.layers[l].datasets.Length; ++d)
                {
                    totalWeightsSizeInBytes += barracudaModel.layers[l].datasets[d].length;
                }
            }
            return totalWeightsSizeInBytes;
        }

        /// <summary>
        /// Compute a hash of the model's layer data and return it as a string.
        /// A subset of the layer weights are used for performance.
        /// This increases the chance of a collision, but this should still be extremely rare.
        /// </summary>
        /// <param name="barracudaModel"></param>
        /// <returns></returns>
        static string GetModelHash(Model barracudaModel)
        {
            // Pre-2020 versions of Unity don't have Hash128.Append() (can only hash strings)
            // For these versions, we'll use a simple wrapper that just supports arrays of floats.
#if UNITY_2020_1_OR_NEWER
            var hash = new Hash128();
#else
            var hash = new MLAgentsHash128();
#endif
            // Limit the max number of float bytes that we hash for performance.
            const int kMaxFloats = 256;

            foreach (var layer in barracudaModel.layers)
            {
                hash.Append(layer.name);
                var numFloatsToHash = Mathf.Min(layer.weights.Length, kMaxFloats);
                hash.Append(layer.weights, 0, numFloatsToHash);
            }

            return hash.ToString();
        }
    }
}