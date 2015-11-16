﻿//   AttitudeController.cs
//
//  Author:
//       Allis Tauri <allista@gmail.com>
//
//  Copyright (c) 2015 Allis Tauri
//
// This work is licensed under the Creative Commons Attribution 4.0 International License. 
// To view a copy of this license, visit http://creativecommons.org/licenses/by/4.0/ 
// or send a letter to Creative Commons, PO Box 1866, Mountain View, CA 94042, USA.

using System;
using UnityEngine;

namespace ThrottleControlledAvionics
{
	public class AttitudeControl : AutopilotModule
	{
		public class Config : ModuleConfig
		{
			new public const string NODE_NAME = "ATC";

			[Persistent] public PID_Controller PID = new PID_Controller(10f, 0.02f, 0.5f, -1, 1);

			[Persistent] public float MinAAf = 0.1f,  MaxAAf = 1f;
			[Persistent] public float InertiaFactor = 10f, AngularMf = 0.002f;
			[Persistent] public float MoIFactor = 0.01f;
			[Persistent] public float AngleThreshold = 25f;
			[Persistent] public float MinEf = 0.001f, MaxEf  = 5f;
		}
		static Config ATC { get { return TCAScenario.Globals.ATC; } }

		Transform  vesselTransform { get { return VSL.vessel.transform; } }
		Orbit orbit { get { return VSL.vessel.orbit; } }

		readonly PIDv_Controller2 pid = new PIDv_Controller2();
		Transform refT;
		Quaternion attitude_error, locked_attitude;
		bool attitude_locked;
		Vector3 thrust, lthrust, needed_lthrust, steering;
		Vector3 angularV { get { return VSL.vessel.angularVelocity; } }
		float omega2, p_omega2, pp_omega2;
		const float steering_norm = Mathf.PI*Mathf.PI;

		public AttitudeControl(VesselWrapper vsl) { VSL = vsl; }

		public override void Init() 
		{ 
			base.Init();
			pid.setPID(ATC.PID);
			reset();
			CFG.AT.AddSingleCallback(Enable);
			#if DEBUG
			RenderingManager.AddToPostDrawQueue(1, RadarBeam);
			#endif
		}

		#if DEBUG
		public void RadarBeam()
		{
			if(VSL == null || VSL.vessel == null || VSL.refT == null || !CFG.AT) return;
			if(!thrust.IsZero())
				GLUtils.GLVec(VSL.wCoM, thrust.normalized*20, Color.red);
			if(!needed_lthrust.IsZero())
				GLUtils.GLVec(VSL.wCoM, VSL.refT.TransformDirection(needed_lthrust.normalized)*20, Color.yellow);
		}

		public override void Reset()
		{
			base.Reset();
			RenderingManager.RemoveFromPostDrawQueue(1, RadarBeam);
		}
		#endif

		protected override void UpdateState() { IsActive = CFG.AT; }

		public override void Enable(bool enable = true)
		{
			reset();
			if(enable) VSL.UpdateOnPlanetStats();
			BlockSAS(enable);
		}

		void reset()
		{
			pid.Reset();
			refT = null;
			VSL.GimbalLimit = 100;
			VSL.AttitudeError = 0;
			omega2 = 0;
			p_omega2 = 0;
			pp_omega2 = 0;
			attitude_locked = false;
		}

		void CalculateSteering()
		{
			Vector3 v;
			omega2 = VSL.vessel.angularVelocity.sqrMagnitude;
			attitude_error = Quaternion.identity;
			needed_lthrust = Vector3.zero;
			switch(CFG.AT.state)
			{
			case Attitude.Custom:
				attitude_error = VSL.CustomRotation;
				break;
			case Attitude.HoldAttitude:
				if(refT != VSL.refT || !attitude_locked)
				{
					refT = VSL.refT;
					locked_attitude = refT.rotation;
					attitude_locked = true;
				}
				if(refT != null)
					attitude_error = Quaternion.Inverse(refT.rotation.Inverse()*locked_attitude);
				break;
			case Attitude.KillRotation:
				if(refT != VSL.refT || p_omega2 <= pp_omega2 && p_omega2 < omega2)
				{
					refT = VSL.refT;
					locked_attitude = refT.rotation;
				}
				if(refT != null)
					attitude_error = Quaternion.Inverse(refT.rotation.Inverse()*locked_attitude);
				break;
			case Attitude.Prograde:
				v = VSL.vessel.situation == Vessel.Situations.ORBITING ||
					VSL.vessel.situation == Vessel.Situations.SUB_ORBITAL? 
					-VSL.vessel.obt_velocity : -VSL.vessel.srf_velocity;
				needed_lthrust = VSL.refT.InverseTransformDirection(v.normalized);
				break;
			case Attitude.Retrograde:
				v = VSL.vessel.situation == Vessel.Situations.ORBITING ||
					VSL.vessel.situation == Vessel.Situations.SUB_ORBITAL? 
					VSL.vessel.obt_velocity : VSL.vessel.srf_velocity;
				needed_lthrust = VSL.refT.InverseTransformDirection(v.normalized);
				break;
			case Attitude.Normal:
				needed_lthrust = -VSL.refT.InverseTransformDirection(orbit.h.xzy);
				break;
			case Attitude.AntiNormal:
				needed_lthrust = VSL.refT.InverseTransformDirection(orbit.h.xzy);
				break;
			case Attitude.Radial:
				needed_lthrust = VSL.refT.InverseTransformDirection(Vector3d.Cross(VSL.vessel.obt_velocity.normalized, orbit.h.xzy.normalized));
				break;
			case Attitude.AntiRadial:
				needed_lthrust = -VSL.refT.InverseTransformDirection(Vector3d.Cross(VSL.vessel.obt_velocity.normalized, orbit.h.xzy.normalized));
				break;
			case Attitude.Target:
				if(!VSL.HasTarget) 
				{ CFG.AT.On(Attitude.KillRotation); break; }
				needed_lthrust = VSL.refT.InverseTransformDirection((VSL.wCoM-VSL.vessel.targetObject.GetTransform().position).normalized);
				break;
			case Attitude.AntiTarget:
				if(!VSL.HasTarget) 
				{ CFG.AT.On(Attitude.KillRotation); break; }
				needed_lthrust = VSL.refT.InverseTransformDirection((VSL.vessel.targetObject.GetTransform().position-VSL.wCoM).normalized);
				break;
			case Attitude.ManeuverNode:
				var solver = VSL.vessel.patchedConicSolver;
				if(solver == null || solver.maneuverNodes.Count == 0)
				{ CFG.AT.On(Attitude.KillRotation); break; }
				needed_lthrust = VSL.refT.InverseTransformDirection(-solver.maneuverNodes[0].GetBurnVector(orbit).normalized);
				break;
			}
			if(!needed_lthrust.IsZero())
			{
				thrust = VSL.Thrust.IsZero()? VSL.MaxThrust : VSL.Thrust;
				lthrust = VSL.refT.InverseTransformDirection(thrust).normalized;
				if(Vector3.Angle(needed_lthrust, lthrust) > ATC.AngleThreshold)
				{
					//rotational axis
					var lthrust_maxI = lthrust.MaxI();
					var axis = Vector3.Cross(needed_lthrust, lthrust).Exclude(lthrust_maxI);
					if(axis.sqrMagnitude < 0.01f) 
						axis = VSL.MaxAngularA.Exclude(lthrust_maxI).MaxComponent();
					//main rotation component
					var axis1 = axis.MaxComponent();
					var lthrust_cmp1 = Vector3.ProjectOnPlane(lthrust, axis1);
					var needed_lthrust_cmp1 = Vector3.ProjectOnPlane(needed_lthrust, axis1);
					var angle1 = Vector3.Angle(needed_lthrust_cmp1, lthrust_cmp1);
					//second rotation component
					var axis2 = (axis - axis1).MaxComponent();
					var angle2 = Vector3.Angle(needed_lthrust, needed_lthrust_cmp1);
					//steering
					steering = (axis1.normalized * angle1 + 
					            axis2.normalized * angle2) * Mathf.Deg2Rad;
//					Log("\naxis {0}\naxis1 {1}\naxis2 {2}\nangle1 {3}, angle2 {4}\nsteering {5}",
//					    axis, axis1, axis2, angle1, angle2, steering);//debug
				}
				else attitude_error = Quaternion.FromToRotation(needed_lthrust, lthrust);   
			}
			if(attitude_error != Quaternion.identity)
			{
				steering = new Vector3(Utils.CenterAngle(attitude_error.eulerAngles.x),
				                       Utils.CenterAngle(attitude_error.eulerAngles.y),
				                       Utils.CenterAngle(attitude_error.eulerAngles.z))*Mathf.Deg2Rad;
				#if DEBUG
				thrust = VSL.Thrust.IsZero()? VSL.MaxThrust : VSL.Thrust;
				lthrust = VSL.refT.InverseTransformDirection(thrust).normalized;
				needed_lthrust = attitude_error.Inverse()*lthrust;
				#endif
			}
			VSL.ResetCustomRotation();
			pp_omega2 = p_omega2;
			p_omega2 = omega2;
		}

		protected override void OnAutopilotUpdate(FlightCtrlState s)
		{
			//need to check all the prerequisites, because the callback is called asynchroniously
			if(!(CFG.Enabled && CFG.AT && VSL.refT != null && orbit != null)) return;
			DisableSAS();
			VSL.GimbalLimit = 100;
			if(VSL.AutopilotDisabled) { reset(); return; }
			//calculate needed steering
			CalculateSteering();
			VSL.AttitudeError = steering.magnitude*Mathf.Rad2Deg;
			var control = new Vector3(steering.x.Equals(0)? 0 : 1,
			                          steering.y.Equals(0)? 0 : 1,
			                          steering.z.Equals(0)? 0 : 1);
			//tune PID parameters
			var angularM = Vector3.Scale(angularV, VSL.MoI);
			var AAf = Mathf.Clamp(1/VSL.MaxAngularA_m, ATC.MinAAf, ATC.MaxAAf);
			var Ef = Utils.Clamp(steering.sqrMagnitude/steering_norm, ATC.MinEf, 1);
			var PIf = AAf*Utils.ClampL(1-Ef, 0.5f)*ATC.MaxEf;
			pid.P = ATC.PID.P*PIf;
			pid.I = ATC.PID.I*PIf;
			pid.D = ATC.PID.D*Utils.ClampH(Utils.ClampL(1-Ef*2, 0)+angularM.magnitude*ATC.AngularMf, 1)*AAf*AAf;
			//set gimbal limit
			VSL.GimbalLimit = Ef*100;
//			Log("Ef: {0}", Ef);//debug
			//tune steering
			steering.Scale(Vector3.Scale(VSL.MaxAngularA.Exclude(steering.MinI()), control).Inverse(0).normalized);
			var inertia  = Vector3.Scale(angularM.Sign(),
			                             Vector3.Scale(Vector3.Scale(angularM, angularM),
			                                           Vector3.Scale(VSL.MaxTorque, VSL.MoI).Inverse(0)))
				.ClampComponents(-Mathf.PI, Mathf.PI);
			steering += inertia / Mathf.Lerp(ATC.InertiaFactor, 1, VSL.MoI.magnitude*ATC.MoIFactor);
			//update PID controller and set steering
			pid.Update(steering, angularV);
			SetRot(pid.Action, s);

			#if DEBUG
//			Log("\nTf {0}\nMoI {1}\nangularV {2}\nangularM {3}\nmaxAA {4}\n" +
//				"inertia {5}\nmaxAAmod {6}\nsteering {7}\naction {8}\npid {9}\n" +
//			    "GimbalLimit {10}",
//			    AAf, VSL.MoI, angularV, angularM, VSL.MaxAngularA, inertia, 
//			    Vector3.Scale(VSL.MaxAngularA.Exclude(steering.MinI()), control).Inverse(0).normalized,
//			    steering, pid.Action, pid, VSL.GimbalLimit);//debug
			ThrottleControlledAvionics.DebugMessage = 
				string.Format("pid: {0}\nerror: {1}°\ngimbal limit: {2}",
				              pid, steering*Mathf.Rad2Deg, VSL.GimbalLimit);
			#endif
		}
	}
}
