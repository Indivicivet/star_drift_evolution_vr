using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace StarDriftEvolutionVR
{
    internal sealed class StereoRenderer : IDisposable
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void RenderEvent(int eventId);

        private enum RenderOperation
        {
            ReadPose,
            Submit,
            Shutdown,
        }

        private readonly OpenVr vr;
        private readonly Camera eyeCamera;
        private readonly Skybox eyeSkybox;
        private readonly object renderStateLock = new object();
        private readonly RenderEvent renderEvent;
        private readonly IntPtr renderEventPointer;
        private readonly RenderTexture[] targets = new RenderTexture[2];
        private readonly IntPtr[] nativeTextures = new IntPtr[2];
        private bool recenter = true;
        private Vector3 centerPosition;
        private Quaternion inverseCenterYaw;
        private OpenVr.Matrix34 headPose;
        private bool tracked;
        private OpenVr.CompositorError trackingError;
        private OpenVr.CompositorError submitError;
        private Exception renderThreadError;
        private bool submittedStereo;

        public static StereoRenderer Start(float resolutionScale)
        {
            OpenVr vr = OpenVr.Start();
            try
            {
                return new StereoRenderer(vr, resolutionScale);
            }
            catch
            {
                vr.Dispose();
                throw;
            }
        }

        private StereoRenderer(OpenVr vr, float resolutionScale)
        {
            this.vr = vr;
            renderEvent = ProcessRenderEvent;
            renderEventPointer = Marshal.GetFunctionPointerForDelegate(renderEvent);
            var rig = new GameObject("Star Drift Evolution VR Camera");
            UnityEngine.Object.DontDestroyOnLoad(rig);
            eyeCamera = rig.AddComponent<Camera>();
            eyeCamera.enabled = false;
            eyeSkybox = rig.AddComponent<Skybox>();

            try
            {
                int width = Mathf.Clamp(
                    Mathf.RoundToInt(vr.EyeWidth * resolutionScale), 1, SystemInfo.maxTextureSize
                );
                int height = Mathf.Clamp(
                    Mathf.RoundToInt(vr.EyeHeight * resolutionScale), 1, SystemInfo.maxTextureSize
                );
                for (int eye = 0; eye < targets.Length; eye++)
                {
                    targets[eye] = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32)
                    {
                        name = eye == 0 ? "Star Drift VR Left Eye" : "Star Drift VR Right Eye",
                    };
                    CreateTarget(eye);
                }
            }
            catch
            {
                ReleaseCameraAndTargets();
                throw;
            }
        }

        public void Recenter() => recenter = true;
        public bool QuitRequested() => vr.QuitRequested();

        public OpenVr.CompositorError Render(Camera source, float worldScale, out bool hasSubmittedFrame)
        {
            // WaitGetPoses may touch the D3D11 context cached by Submit.
            QueueRenderEvent(RenderOperation.ReadPose);
            SynchronizeRenderThread();

            OpenVr.Matrix34 head;
            lock (renderStateLock)
            {
                hasSubmittedFrame = submittedStereo;
                if (renderThreadError != null)
                {
                    throw new InvalidOperationException("OpenVR render callback failed.", renderThreadError);
                }

                if (submitError != OpenVr.CompositorError.None)
                {
                    OpenVr.CompositorError result = submitError;
                    submitError = OpenVr.CompositorError.None;
                    return result;
                }

                if (trackingError != OpenVr.CompositorError.None || !tracked)
                {
                    return trackingError;
                }
                head = headPose;
            }

            Quaternion headRotation = head.Rotation;
            if (recenter)
            {
                centerPosition = head.Position;
                Vector3 forward = headRotation * Vector3.forward;
                float yaw = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
                inverseCenterYaw = Quaternion.Euler(0f, -yaw, 0f);
                recenter = false;
            }

            Transform anchor = source.transform;
            Quaternion trackingToWorld = anchor.rotation * inverseCenterYaw;
            Vector3 headPosition = anchor.position + trackingToWorld * ((head.Position - centerPosition) * worldScale);
            Quaternion headToWorld = trackingToWorld * headRotation;

            eyeCamera.CopyFrom(source);
            eyeCamera.enabled = false;
            eyeCamera.stereoTargetEye = StereoTargetEyeMask.None;
            eyeCamera.rect = new Rect(0f, 0f, 1f, 1f);
            eyeCamera.allowHDR = false;
            eyeCamera.allowMSAA = false;
            eyeCamera.ResetWorldToCameraMatrix();
            eyeCamera.ResetCullingMatrix();

            Skybox sourceSkybox = source.GetComponent<Skybox>();
            eyeSkybox.enabled = sourceSkybox != null && sourceSkybox.enabled;
            eyeSkybox.material = eyeSkybox.enabled ? sourceSkybox.material : null;

            for (int eye = 0; eye < targets.Length; eye++)
            {
                if (!targets[eye].IsCreated())
                {
                    CreateTarget(eye);
                }
                OpenVr.Matrix34 eyeToHead = vr.EyeToHead(eye);
                eyeCamera.transform.SetPositionAndRotation(
                    headPosition + headToWorld * (eyeToHead.Position * worldScale),
                    headToWorld * eyeToHead.Rotation
                );
                eyeCamera.targetTexture = targets[eye];
                eyeCamera.projectionMatrix = vr.EyeProjection(
                    eye, eyeCamera.nearClipPlane, eyeCamera.farClipPlane
                );
                eyeCamera.Render();
            }
            eyeCamera.targetTexture = null;

            QueueRenderEvent(RenderOperation.Submit);
            return OpenVr.CompositorError.None;
        }

        private void QueueRenderEvent(RenderOperation operation)
        {
            GL.IssuePluginEvent(renderEventPointer, (int)operation);
            GL.InvalidateState();
        }

        private void SynchronizeRenderThread()
        {
            // Unity 2017 synchronizes its graphics queue before handing out a native texture.
            targets[0].GetNativeTexturePtr();
        }

        private void CreateTarget(int eye)
        {
            if (!targets[eye].Create())
            {
                throw new InvalidOperationException("Could not create an OpenVR eye texture.");
            }
            IntPtr nativeTexture = targets[eye].GetNativeTexturePtr();
            if (nativeTexture == IntPtr.Zero)
            {
                throw new InvalidOperationException("Could not get the native eye texture.");
            }
            lock (renderStateLock)
            {
                nativeTextures[eye] = nativeTexture;
            }
        }

        private void ProcessRenderEvent(int eventId)
        {
            lock (renderStateLock)
            {
                try
                {
                    switch ((RenderOperation)eventId)
                    {
                        case RenderOperation.ReadPose:
                            trackingError = vr.ReadHeadPose(out headPose, out tracked);
                            break;
                        case RenderOperation.Submit:
                            OpenVr.CompositorError left = vr.Submit(0, nativeTextures[0]);
                            OpenVr.CompositorError right = vr.Submit(1, nativeTextures[1]);
                            submitError = left == OpenVr.CompositorError.None ? right : left;
                            submittedStereo |= submitError == OpenVr.CompositorError.None;
                            break;
                        case RenderOperation.Shutdown:
                            vr.Dispose();
                            break;
                    }
                }
                catch (Exception error)
                {
                    // Exceptions must stay on the managed side of the render callback.
                    renderThreadError = error;
                }
            }
        }

        public void Dispose()
        {
            try
            {
                QueueRenderEvent(RenderOperation.Shutdown);
                SynchronizeRenderThread();
            }
            finally
            {
                ReleaseCameraAndTargets();
            }
        }

        private void ReleaseCameraAndTargets()
        {
            eyeCamera.targetTexture = null;
            UnityEngine.Object.Destroy(eyeCamera.gameObject);
            foreach (RenderTexture target in targets)
            {
                if (target != null)
                {
                    target.Release();
                    UnityEngine.Object.Destroy(target);
                }
            }
        }
    }
}
