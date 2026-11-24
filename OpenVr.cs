using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace StarDriftEvolutionVR
{
    internal sealed class OpenVr : IDisposable
    {
        // The slot numbers below are fixed by these versioned OpenVR C interfaces.
        private const string SystemVersion = "FnTable:IVRSystem_019";
        private const string CompositorVersion = "FnTable:IVRCompositor_022";
        private const int SceneApplication = 1;
        private const int SeatedTracking = 0;
        private const int DirectXTexture = 0;
        private const int GammaColor = 1;
        private const uint QuitEvent = 700;
        private const uint EventSize = 48;
        private const string Library = "openvr_api";

        private readonly GetEyeTransform getEyeTransform;
        private readonly GetProjectionRaw getProjectionRaw;
        private readonly PollNextEvent pollNextEvent;
        private readonly AcknowledgeQuit acknowledgeQuit;
        private readonly WaitGetPoses waitGetPoses;
        private readonly SubmitTexture submitTexture;
        private readonly ClearFrame clearFrame;
        private readonly int width;
        private readonly int height;

        public static OpenVr Start()
        {
            VR_InitInternal(out int error, SceneApplication);
            if (error != 0)
            {
                throw new InvalidOperationException("OpenVR initialization failed: " + DescribeError(error));
            }

            try
            {
                return new OpenVr();
            }
            catch
            {
                VR_ShutdownInternal();
                throw;
            }
        }

        private OpenVr()
        {
            IntPtr system = GetInterface(SystemVersion);
            IntPtr compositor = GetInterface(CompositorVersion);
            Bind<GetRecommendedSize>(system, 0)(out uint recommendedWidth, out uint recommendedHeight);
            width = checked((int)recommendedWidth);
            height = checked((int)recommendedHeight);
            if (width <= 0 || height <= 0)
            {
                throw new InvalidOperationException("OpenVR returned an invalid eye texture size.");
            }

            getProjectionRaw = Bind<GetProjectionRaw>(system, 2);
            getEyeTransform = Bind<GetEyeTransform>(system, 4);
            pollNextEvent = Bind<PollNextEvent>(system, 30);
            acknowledgeQuit = Bind<AcknowledgeQuit>(system, 45);
            Bind<SetTrackingSpace>(compositor, 0)(SeatedTracking);
            waitGetPoses = Bind<WaitGetPoses>(compositor, 2);
            submitTexture = Bind<SubmitTexture>(compositor, 5);
            clearFrame = Bind<ClearFrame>(compositor, 6);
        }

        public int EyeWidth => width;
        public int EyeHeight => height;

        public bool QuitRequested()
        {
            while (pollNextEvent(out VrEvent vrEvent, EventSize))
            {
                if (vrEvent.Type == QuitEvent)
                {
                    acknowledgeQuit();
                    return true;
                }
            }

            return false;
        }

        public CompositorError ReadHeadPose(out Matrix34 pose, out bool tracked)
        {
            CompositorError error = waitGetPoses(out TrackedPose device, 1, IntPtr.Zero, 0);
            pose = device.Transform;
            tracked = device.PoseIsValid != 0 && device.DeviceIsConnected != 0;
            return error;
        }

        public Matrix34 EyeToHead(int eye) => getEyeTransform(eye);

        public Matrix4x4 EyeProjection(int eye, float near, float far)
        {
            getProjectionRaw(eye, out float left, out float right, out float bottom, out float top);
            return Matrix4x4.Frustum(left * near, right * near, bottom * near, top * near, near, far);
        }

        public CompositorError Submit(int eye, IntPtr nativeTexture)
        {
            var texture = new Texture
            {
                Handle = nativeTexture,
                Type = DirectXTexture,
                ColorSpace = GammaColor,
            };
            // Unity 2017 renders D3D11 camera targets with the image upside down.
            var bounds = new TextureBounds { UMin = 0f, UMax = 1f, VMin = 1f, VMax = 0f };
            return submitTexture(eye, ref texture, ref bounds, 0);
        }

        public void Dispose()
        {
            clearFrame();
            VR_ShutdownInternal();
        }

        private static IntPtr GetInterface(string version)
        {
            IntPtr table = VR_GetGenericInterface(version, out int error);
            if (table == IntPtr.Zero || error != 0)
            {
                throw new InvalidOperationException(version + ": " + DescribeError(error));
            }

            return table;
        }

        private static T Bind<T>(IntPtr table, int slot) where T : Delegate
        {
            IntPtr address = Marshal.ReadIntPtr(table, slot * IntPtr.Size);
            return (T)Marshal.GetDelegateForFunctionPointer(address, typeof(T));
        }

        private static string DescribeError(int error)
        {
            return Marshal.PtrToStringAnsi(VR_GetVRInitErrorAsEnglishDescription(error)) + " (" + error + ")";
        }

        internal enum CompositorError
        {
            None = 0,
            DoNotHaveFocus = 101,
            InvalidTexture = 102,
            TextureIsOnWrongDevice = 104,
            TextureUsesUnsupportedFormat = 105,
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Matrix34
        {
            private float m00, m01, m02, m03;
            private float m10, m11, m12, m13;
            private float m20, m21, m22, m23;

            // Reflect across Z to convert OpenVR's -Z forward to Unity's +Z forward.
            public Vector3 Position => new Vector3(m03, m13, -m23);

            public Quaternion Rotation => Quaternion.LookRotation(
                new Vector3(-m02, -m12, m22),
                new Vector3(m01, m11, -m21)
            );
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TrackedPose
        {
            public Matrix34 Transform;
            private Vector3 velocity;
            private Vector3 angularVelocity;
            private int trackingResult;
            public byte PoseIsValid;
            public byte DeviceIsConnected;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Texture
        {
            public IntPtr Handle;
            public int Type;
            public int ColorSpace;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TextureBounds
        {
            public float UMin;
            public float VMin;
            public float UMax;
            public float VMax;
        }

        // Only the event type is needed. Leave room for the native event payload on Windows.
        [StructLayout(LayoutKind.Sequential, Size = (int)EventSize)]
        private struct VrEvent
        {
            public uint Type;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void GetRecommendedSize(out uint width, out uint height);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate Matrix34 GetEyeTransform(int eye);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void GetProjectionRaw(
            int eye, out float left, out float right, out float bottom, out float top
        );

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool PollNextEvent(out VrEvent vrEvent, uint size);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void AcknowledgeQuit();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void SetTrackingSpace(int trackingOrigin);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate CompositorError WaitGetPoses(
            out TrackedPose renderPose, uint renderPoseCount, IntPtr gamePoses, uint gamePoseCount
        );

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate CompositorError SubmitTexture(
            int eye, ref Texture texture, ref TextureBounds bounds, int flags
        );

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void ClearFrame();

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint VR_InitInternal(out int error, int applicationType);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern void VR_ShutdownInternal();

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern IntPtr VR_GetGenericInterface(string version, out int error);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr VR_GetVRInitErrorAsEnglishDescription(int error);
    }
}
