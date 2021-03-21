﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Numerics;
using System.Threading;
using System.IO;
using System.IO.MemoryMappedFiles;
using CMCustomUDP;
using NoiseFilters;
using Newtonsoft.Json;
using System.Windows.Forms;
using System.Diagnostics;

namespace GenericTelemetryProvider
{
    public class GenericProviderBase
    {
        protected Mutex mutex;
        protected MemoryMappedFile filteredMMF;
        protected CMCustomUDPData lastFilteredData;
        protected CMCustomUDPData filteredData;
        protected CMCustomUDPData rawData;
        protected bool lastFrameValid = false;
        protected Vector3 lastVelocity = Vector3.Zero;
        protected Vector3 lastPosition = Vector3.Zero;
        protected Vector3 lastWorldVelocity = Vector3.Zero;
        protected Vector3 lastRawPos = Vector3.Zero;

        protected TelemetrySender telemetrySender = new TelemetrySender();
        protected bool sendUDP = false;
        protected bool fillMMF = false;

        protected NestedSmooth dtFilter = new NestedSmooth(0, 60, 100000.0f);

        protected Vector3 rht;
        protected Vector3 up;
        protected Vector3 fwd;

        protected Vector3 worldPosition;

        protected Matrix4x4 transform;
        public static float dt;
        protected float maxAccel2DMagSusp = 3.0f;

        protected int posKeyMask = CMCustomUDPData.GetKeyMask(CMCustomUDPData.DataKey.position_x, CMCustomUDPData.DataKey.position_y, CMCustomUDPData.DataKey.position_z);
        protected int velKeyMask = CMCustomUDPData.GetKeyMask(CMCustomUDPData.DataKey.local_velocity_x, CMCustomUDPData.DataKey.local_velocity_y, CMCustomUDPData.DataKey.local_velocity_z);
        protected int angVelKeyMask = CMCustomUDPData.GetKeyMask(CMCustomUDPData.DataKey.yaw_velocity, CMCustomUDPData.DataKey.roll_velocity, CMCustomUDPData.DataKey.pitch_velocity);
        protected int suspVelKeyMask = CMCustomUDPData.GetKeyMask(CMCustomUDPData.DataKey.suspension_velocity_bl, CMCustomUDPData.DataKey.suspension_velocity_br, CMCustomUDPData.DataKey.suspension_velocity_fl, CMCustomUDPData.DataKey.suspension_velocity_fr);
        protected int accelKeyMask = CMCustomUDPData.GetKeyMask(CMCustomUDPData.DataKey.gforce_lateral, CMCustomUDPData.DataKey.gforce_vertical, CMCustomUDPData.DataKey.gforce_longitudinal);

        protected Hotkey hotkey;
        protected bool telemetryPaused = false;
        protected float telemetryPausedTimer = 0.0f;
        protected float telemetryPausedTime = 3.0f;
        public Form gameUI;
        public int updateDelay = 10;

        bool isStopped = false;
        Mutex isStoppedMutex = new Mutex(false);

        public bool IsStopped
        {
            get
            {
                bool rval = false;
                isStoppedMutex.WaitOne();
                rval = isStopped;
                isStoppedMutex.ReleaseMutex();
                return rval;
            }
            set
            {
                isStoppedMutex.WaitOne();
                isStopped = value;
                isStoppedMutex.ReleaseMutex();
            }
        }


        public virtual void Run()
        {
            mutex = new Mutex(false, "GenericTelemetryProviderMutex");

            filteredMMF = MemoryMappedFile.CreateOrOpen("GenericTelemetryProviderFiltered", 10000);

            if (MainConfig.Instance.configData.hotkey.enabled)
            {
                hotkey = new Hotkey();
                hotkey.KeyCode = MainConfig.Instance.configData.hotkey.key;
                hotkey.Windows = MainConfig.Instance.configData.hotkey.windows;
                hotkey.Alt = MainConfig.Instance.configData.hotkey.alt;
                hotkey.Shift = MainConfig.Instance.configData.hotkey.shift;
                hotkey.Control = MainConfig.Instance.configData.hotkey.ctrl;
                hotkey.Pressed += delegate {
                    telemetryPaused = !telemetryPaused;
                    telemetryPausedTimer = telemetryPausedTime - telemetryPausedTimer;
                };

                if (hotkey.Register(gameUI))
                {
                    //giggity
                }

            }

            //this will end any threads already running
            IsStopped = true;
        }

        public virtual void StartSending()
        {
            lastPosition = Vector3.Zero;
            lastFrameValid = false;
            lastVelocity = Vector3.Zero;
            lastWorldVelocity = Vector3.Zero;
            lastRawPos = Vector3.Zero;

            filteredData = new CMCustomUDPData();
            filteredData.Init();
            rawData = new CMCustomUDPData();
            rawData.Init();

            fillMMF = MainConfig.Instance.configData.fillMMF;
            sendUDP = MainConfig.Instance.configData.sendUDP;

            if (sendUDP)
            {
                telemetrySender.StartSending(MainConfig.Instance.configData.udpIP, MainConfig.Instance.configData.udpPort);
            }

            IsStopped = false;

        }

        public virtual void StopSending()
        {
            if (sendUDP)
                telemetrySender.StopSending();
        }

        public virtual void Stop()
        {
            if (filteredMMF != null)
                filteredMMF.Dispose();
            filteredMMF = null;

            if (hotkey != null && hotkey.Registered)
                hotkey.Unregister(); 

        }

        public virtual bool ProcessTransform(Matrix4x4 inTransform, float inDT)
        {
            transform = inTransform;
            dt = inDT;

            if (!ExtractFwdUpRht())
                return false;

            if (!CheckLastFrameValid())
                return true;

            FilterDT();

            if (CalcPosition())
            {

                CalcVelocity();

                FilterVelocity();

                CalcAcceleration();

                CalcAngles();


                /*
                 //debug
                using (MemoryMappedViewStream stream = mmf.CreateViewStream())
                {
                    BinaryReader reader = new BinaryReader(stream);
                    byte[] readBuffer = reader.ReadBytes((int)stream.Length);

                    var alloc = GCHandle.Alloc(readBuffer, GCHandleType.Pinned);
                    GenericProviderData readTelemetry = (GenericProviderData)Marshal.PtrToStructure(alloc.AddrOfPinnedObject(), typeof(GenericProviderData));
                    alloc.Free();
                }
                */

                CalcAngularVelocityAndAccel();

                SimulateSuspension();

                SimulateEngine();

                ProcessInputs();

                //Filter everything besides position, velocity, angular velocity, suspension velocity
                FilterModuleCustom.Instance.Filter(rawData, ref filteredData, int.MaxValue & ~(posKeyMask | velKeyMask | angVelKeyMask | suspVelKeyMask | accelKeyMask), false);

                HandleTelemetryPaused();

            }
            else
            {
                filteredData.Copy(lastFilteredData);
            }    



            return true;
        }


        public virtual bool ExtractFwdUpRht()
        {
            rht = new Vector3(transform.M11, transform.M12, transform.M13);
            up = new Vector3(transform.M21, transform.M22, transform.M23);
            fwd = new Vector3(transform.M31, transform.M32, transform.M33);

            float rhtMag = rht.Length();
            float upMag = up.Length();
            float fwdMag = fwd.Length();

            //reading garbage
            if (rhtMag < 0.9f || upMag < 0.9f || fwdMag < 0.9f)
            {
                return false;
            }

            return true;

        }

        public virtual bool CheckLastFrameValid()
        {
            if (!lastFrameValid)
            {
                lastFilteredData = new CMCustomUDPData();
                lastPosition = transform.Translation;
                lastFrameValid = true;
                lastVelocity = Vector3.Zero;
                lastWorldVelocity = Vector3.Zero;
                lastRawPos = Vector3.Zero;
                return false;
            }

            return true;
        }

        public virtual void FilterDT()
        {
            dt = dtFilter.Filter(dt);

            if (dt <= 0)
                dt = 0.015f;

        }

        public virtual bool CalcPosition()
        {
            Vector3 currRawPos = new Vector3(transform.M41, transform.M42, transform.M43);
            
            Vector3 rawVel = (currRawPos - lastRawPos) / dt;
            float rawVelMag = rawVel.Length();
            float lastVelMag = lastWorldVelocity.Length();

            if (rawVelMag <= lastVelMag * 0.25f && (lastVelMag < rawVelMag * 10000.0f || rawVelMag <= float.Epsilon))
            {
                return false;
            }
            
            rawData.position_x = currRawPos.X;
            rawData.position_y = currRawPos.Y;
            rawData.position_z = currRawPos.Z;

            lastRawPos = currRawPos;

            //filter position
            FilterModuleCustom.Instance.Filter(rawData, ref filteredData, posKeyMask, true);

            //assign
            worldPosition = new Vector3((float)filteredData.position_x, (float)filteredData.position_y, (float)filteredData.position_z);

            return true;
        }

        public virtual void CalcVelocity()
        {
            Vector3 worldVelocity = (worldPosition - lastPosition) / dt;
            lastWorldVelocity = worldVelocity;

            lastPosition = transform.Translation = worldPosition;

            Matrix4x4 rotation = new Matrix4x4();
            rotation = transform;
            rotation.M41 = 0.0f;
            rotation.M42 = 0.0f;
            rotation.M43 = 0.0f;
            rotation.M44 = 1.0f;

            Matrix4x4 rotInv = new Matrix4x4();
            Matrix4x4.Invert(rotation, out rotInv);

            //transform world velocity to local space
            Vector3 localVelocity = Vector3.Transform(worldVelocity, rotInv);

            localVelocity.X = -localVelocity.X;

            rawData.local_velocity_x = localVelocity.X;
            rawData.local_velocity_y = localVelocity.Y;
            rawData.local_velocity_z = localVelocity.Z;

        }

        public virtual void FilterVelocity()
        { 

            //filter local velocity
            FilterModuleCustom.Instance.Filter(rawData, ref filteredData, velKeyMask, false);

        }

        public virtual void CalcAcceleration()
        {
            //assign filtered local velocity
            Vector3 localVelocity = new Vector3((float)filteredData.local_velocity_x, (float)filteredData.local_velocity_y, (float)filteredData.local_velocity_z);

            //calculate local acceleration
            Vector3 localAcceleration = ((localVelocity - lastVelocity) / dt) * 0.10197162129779283f; //convert to g accel
            lastVelocity = localVelocity;

            rawData.gforce_lateral = localAcceleration.X;
            rawData.gforce_vertical = localAcceleration.Y;
            rawData.gforce_longitudinal = localAcceleration.Z;

            FilterModuleCustom.Instance.Filter(rawData, ref filteredData, accelKeyMask, false);

        }

        public virtual void CalcAngles()
        {

            Quaternion quat = Quaternion.CreateFromRotationMatrix(transform);

            Vector3 pyr = Utils.GetPYRFromQuaternion(quat);

            rawData.pitch = pyr.X;
            rawData.yaw = pyr.Y;
            rawData.roll = Utils.LoopAngleRad(-pyr.Z, (float)Math.PI * 0.5f);
        }

        public virtual void SimulateSuspension()
        {
            Vector2 accel2D = new Vector2((float)filteredData.gforce_lateral, (float)filteredData.gforce_longitudinal) / 0.10197162129779283f;
            float accel2DMag = accel2D.Length();

            Vector2 accel2DNorm = Vector2.Normalize(accel2D);


            Vector2[] suspensionOffsets = new Vector2[] { new Vector2(-0.5f,-1.0f), //bl
                                                            new Vector2(0.5f,-1.0f), //br
                                                            new Vector2(-0.5f,1.0f), //fl
                                                            new Vector2(0.5f,1.0f)}; //fr


            Vector2[] suspensionVectors = new Vector2[4];

            //calc suspension vectors
            Vector2 centerOfGravity = new Vector2(0.0f, 0.0f);
            for (int i = 0; i < 4; ++i)
            {
                suspensionVectors[i] = Vector2.Normalize(suspensionOffsets[i] - centerOfGravity);
            }

            //suspension travel at rest = -18 to -20
            //rear gets to 7 at maximum acceleration
            //front gets to -75 at max acceleration
            //front gets to 7 at max braking
            //rear gets to -80 at max braking
            float travelCenter = -20.0f;
            float travelMax = 8 - travelCenter;
            float travelMin = -80 - travelCenter;
            float scaledAccelMag = Math.Min(accel2DMag, maxAccel2DMagSusp) / maxAccel2DMagSusp;
            for (int i = 0; i < 4; ++i)
            {
                float dot = Vector2.Dot(accel2DNorm, suspensionVectors[i]);
                float travel = travelCenter;
                float travelMag = 0.0f;

                if(float.IsInfinity(dot) || float.IsNaN(dot))
                {
                    dot = 0;
                }
                else
                if (dot > 0.0f)
                {
                    travelMag = travelMax;
                }
                else
                if (dot < 0.0f)
                {
                    travelMag = travelMin;
                }

                travel += travelMag * Math.Abs(dot) * scaledAccelMag;

                switch (i)
                {
                    case 0:
                        {
                            rawData.suspension_position_bl = filteredData.suspension_position_bl = travel;
                            break;
                        }
                    case 1:
                        {
                            rawData.suspension_position_br = filteredData.suspension_position_br = travel;
                            break;
                        }
                    case 2:
                        {
                            rawData.suspension_position_fl = filteredData.suspension_position_fl = travel;
                            break;
                        }
                    case 3:
                        {
                            rawData.suspension_position_fr = filteredData.suspension_position_fr = travel;
                            break;
                        }
                }
            }


            rawData.suspension_velocity_bl = ((float)filteredData.suspension_position_bl - (float)lastFilteredData.suspension_position_bl) / dt;
            rawData.suspension_velocity_br = ((float)filteredData.suspension_position_br - (float)lastFilteredData.suspension_position_br) / dt;
            rawData.suspension_velocity_fl = ((float)filteredData.suspension_position_fl - (float)lastFilteredData.suspension_position_fl) / dt;
            rawData.suspension_velocity_fr = ((float)filteredData.suspension_position_fr - (float)lastFilteredData.suspension_position_fr) / dt;

            FilterModuleCustom.Instance.Filter(rawData, ref filteredData, suspVelKeyMask, false);

            rawData.suspension_acceleration_bl = ((float)filteredData.suspension_velocity_bl - (float)lastFilteredData.suspension_velocity_bl) / dt;
            rawData.suspension_acceleration_br = ((float)filteredData.suspension_velocity_br - (float)lastFilteredData.suspension_velocity_br) / dt;
            rawData.suspension_acceleration_fl = ((float)filteredData.suspension_velocity_fl - (float)lastFilteredData.suspension_velocity_fl) / dt;
            rawData.suspension_acceleration_fr = ((float)filteredData.suspension_velocity_fr - (float)lastFilteredData.suspension_velocity_fr) / dt;

            //calc wheel patch speed.
            rawData.wheel_patch_speed_bl = filteredData.local_velocity_z;
            rawData.wheel_patch_speed_br = filteredData.local_velocity_z;
            rawData.wheel_patch_speed_fl = filteredData.local_velocity_z;
            rawData.wheel_patch_speed_fr = filteredData.local_velocity_z;
        }

        public virtual void CalcAngularVelocityAndAccel()
        {
            rawData.yaw_velocity = Utils.CalculateAngularChange((float) lastFilteredData.yaw, (float) filteredData.yaw) / dt;
            rawData.pitch_velocity = Utils.CalculateAngularChange((float) lastFilteredData.pitch, (float) filteredData.pitch) / dt;
            rawData.roll_velocity = Utils.CalculateAngularChange((float) lastFilteredData.roll, (float) filteredData.roll) / dt;

            FilterModuleCustom.Instance.Filter(rawData, ref filteredData, angVelKeyMask, false);

            rawData.yaw_acceleration = ((float) filteredData.yaw_velocity - (float) lastFilteredData.yaw_velocity) / dt;
            rawData.pitch_acceleration = ((float) filteredData.pitch_velocity - (float) lastFilteredData.pitch_velocity) / dt;
            rawData.roll_acceleration = ((float) filteredData.roll_velocity - (float) lastFilteredData.roll_velocity) / dt;

        }

        public virtual void SimulateEngine()
        {
            rawData.max_rpm = 6000;
            rawData.max_gears = 6;
            rawData.gear = 1;
            rawData.idle_rpm = 700;

            Vector3 localVelocity = new Vector3((float)filteredData.local_velocity_x, (float)filteredData.local_velocity_y, (float)filteredData.local_velocity_z);

            rawData.speed = localVelocity.Length();

        }

        public virtual void ProcessInputs()
        {
            InputModule.Instance.Update();

            filteredData.engine_rate = (InputModule.Instance.controller.rightTrigger * 5500) + 700;
            filteredData.steering_input = InputModule.Instance.controller.leftThumb.X;
            filteredData.throttle_input = InputModule.Instance.controller.rightTrigger;
            filteredData.brake_input = InputModule.Instance.controller.leftTrigger;
        }

        public virtual void SendFilteredData()
        {

            byte[] bytes = filteredData.GetBytes();

            mutex.WaitOne();

            if (fillMMF)
            {
                using (MemoryMappedViewStream stream = filteredMMF.CreateViewStream())
                {
                    BinaryWriter writer = new BinaryWriter(stream);
                    writer.Write(bytes);
                }
            }

            if (sendUDP)
                telemetrySender.SendAsync(bytes);

            lastFilteredData.Copy(filteredData);

            mutex.ReleaseMutex();

        }

        public virtual void HandleTelemetryPaused()
        {
            filteredData.paused = rawData.paused = telemetryPaused ? 1 : 0;

            if(telemetryPausedTimer > 0.0f || telemetryPaused)
            {
                telemetryPausedTimer = Math.Max(0.0f, telemetryPausedTimer - dt);

                float lerp = telemetryPausedTimer / telemetryPausedTime;
                filteredData.LerpAllFromZero(telemetryPaused ? lerp : 1.0f - lerp);
            }
        }

        public virtual void StopAllThreads()
        {
            IsStopped = true;

        }
    }
}
