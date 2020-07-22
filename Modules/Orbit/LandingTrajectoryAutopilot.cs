//  Author:
//       Allis Tauri <allista@gmail.com>
//
//  Copyright (c) 2016 Allis Tauri
//
// This work is licensed under the Creative Commons Attribution-ShareAlike 4.0 International License. 
// To view a copy of this license, visit http://creativecommons.org/licenses/by-sa/4.0/ 
// or send a letter to Creative Commons, PO Box 1866, Mountain View, CA 94042, USA.
//

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using AT_Utils;
using AT_Utils.UI;
using JetBrains.Annotations;
using UnityEngine;

namespace ThrottleControlledAvionics
{
    public abstract class LandingTrajectoryAutopilot : TargetedTrajectoryCalculator<LandingTrajectory>
    {
        [SuppressMessage("ReSharper", "FieldCanBeMadeReadOnly.Global"),
         SuppressMessage("ReSharper", "ConvertToConstant.Global")]
        public new class Config : ComponentConfig<Config>
        {
            [Persistent] public float Dtol = 1000f; //m
            [Persistent] public float FlyOverAlt = 1000; //m
            [Persistent] public float ApproachAlt = 250; //m

            [Persistent] public float BrakeThrustThreshold = 100; //m/s
            [Persistent] public float BrakeEndSpeed = 10; //m/s
            [Persistent] public float LandingThrustTime = 3; //s

            [Persistent] public float CorrectionOffset = 20f; //s
            [Persistent] public float CorrectionTimer = 10f; //s
            [Persistent] public float CorrectionMinDv = 0.5f; //m/s
            [Persistent] public float CorrectionThrustF = 2.0f;
            [Persistent] public float CorrectionTimeF = 2f;
            [Persistent] public float CorrectionDirF = 2f;

            [Persistent] public float HoverTimeThreshold = 60f; //s
            [Persistent] public float DropBallastThreshold = 0.5f; //dP/P_asl
            [Persistent] public float MaxDPressure = 3f; //kPa
            [Persistent] public float MinDPressure = 1f; //kPa
            [Persistent] public float MachThreshold = 0.9f;

            [Persistent] public float ScanningAngle = 21;
            [Persistent] public int PointsPerFrame = 5;
            [Persistent] public int AtmoTrajectoryResolution = 5;
            [Persistent] public float MaxCorrectionDist = 1;

            [Persistent] public float HeatingCoefficient = 0.02f;

            [Persistent] public float DragCurveF = 0.1f;
        }

        public new static Config C => Config.INST;

        protected LandingTrajectoryAutopilot(ModuleTCA tca) : base(tca) { }

        public enum LandingStage
        {
            None = 0,
            Wait = 2,
            Decelerate = 3,
            Coast = 4,
            HardLanding = 5,
            SoftLanding = 6,
            Approach = 7,
            Land = 8,
            LandHere = 9
        }

        // ReSharper disable MemberCanBePrivate.Global
        // ReSharper disable FieldCanBeMadeReadOnly.Global
        [Persistent] public LandingStage landing_stage;
        [Persistent] public FloatField CorrectionMaxDist = new FloatField(min: 0);
        [Persistent] public bool UseChutes = true;
        [Persistent] public bool UseBrakes = true;
        [Persistent] public bool CorrectTarget = true;

        [Persistent] public bool LandASAP;
        // ReSharper restore FieldCanBeMadeReadOnly.Global
        // ReSharper restore MemberCanBePrivate.Global

        public bool ShowOptions { get; protected set; }

        private TrajectoryRenderer trajectory_renderer;
        protected LandingTrajectory landing_trajectory;
        protected ManeuverExecutor Executor;
        private FuzzyThreshold<double> lateral_angle;

        private readonly Timer DecelerationTimer = new Timer(0.5);
        private readonly Timer CollisionTimer = new Timer();
        private readonly Timer StageTimer = new Timer(5);
        private readonly Timer NoEnginesTimer = new Timer();

        private PQS_Scanner_CDOS scanner;
        private bool scanned, flat_target;

        private readonly Timer dP_up_timer = new Timer();
        private readonly Timer dP_down_timer = new Timer();
        private double pressureASL;
        private double dP_threshold;
        private double landing_deadzone;
        private double terminal_velocity;
        private double last_dP;
        private double rel_dP;
        private float last_Err;

        private bool vessel_within_range;
        private bool vessel_after_target;
        private bool target_within_range;
        private bool landing_before_target;

        [UsedImplicitly] protected AttitudeControl ATC;
        [UsedImplicitly] protected ThrottleControl THR;
        [UsedImplicitly] protected BearingControl BRC;
        [UsedImplicitly] protected AutoLander LND;
        [UsedImplicitly] protected CollisionPreventionSystem CPS;

        protected double TargetAltitude => CFG.Target.SurfaceAlt(Body);

        public override LandingTrajectory CurrentTrajectory =>
            new LandingTrajectory(VSL, Vector3d.zero, VSL.Physics.UT, CFG.Target, TargetAltitude, false);

        protected abstract class LandingSiteOptimizerBase : TrajectoryOptimizer
        {
            private readonly LandingTrajectoryAutopilot module;
            private readonly double dtol;

            public LandingTrajectory Best { get; protected set; }

            public string Status
            {
                get
                {
                    if(Best == null)
                        return "Computing landing trajectory...";
                    return string.Format("Computing landing trajectory.\n" + "Landing site error: {0}",
                        Utils.formatBigValue((float)Best.DistanceToTarget, "m"));
                }
            }

            protected LandingSiteOptimizerBase(LandingTrajectoryAutopilot module, float dtol)
            {
                this.module = module;
                this.dtol = dtol;
            }

            public abstract IEnumerator<LandingTrajectory> GetEnumerator();

            IEnumerator IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }

            protected double dR2dV(double dR)
            {
                return dR * (10 - module.CFG.Target.AngleTo(module.VSL) / Math.PI * 9.9) * module.Body.GeeASL;
            }

            protected bool continue_calculation(LandingTrajectory prev, LandingTrajectory cur)
            {
                return Best == null
                       || prev == null
                       || Best.DistanceToTarget > dtol
                       && (Math.Abs(cur.DeltaR - prev.DeltaR) > 1e-5 || Math.Abs(cur.DeltaFi - prev.DeltaFi) > 1e-5);
            }
        }

        public override void Init()
        {
            base.Init();
            CorrectionMaxDist.Value = C.MaxCorrectionDist;
            CorrectionTimer.Period = C.CorrectionTimer;
            StageTimer.action = () =>
            {
                VSL.ActivateNextStage();
                Message("Have to drop ballast to decelerate...");
            };
            dP_up_timer.action = () =>
            {
                dP_threshold = Utils.ClampL(dP_threshold * 0.9, C.MinDPressure);
                last_dP = VSL.vessel.dynamicPressurekPa;
            };
            dP_down_timer.action = () =>
            {
                dP_threshold = Utils.ClampH(dP_threshold * 1.1, C.MaxDPressure);
                last_dP = VSL.vessel.dynamicPressurekPa;
            };
            NoEnginesTimer.action = () => { landing_stage = LandingStage.HardLanding; };
            Executor = new ManeuverExecutor(TCA);
            scanner = new PQS_Scanner_CDOS(VSL, AutoLander.C.MaxUnevenness / 3);
            lateral_angle = new FuzzyThreshold<double>(AttitudeControlBase.C.MaxAttitudeError,
                AttitudeControlBase.C.AttitudeErrorThreshold);
            dP_threshold = C.MaxDPressure;
            last_Err = 0;
            last_dP = 0;
            Working = false;
            scanned = false;
        }

        protected override void Reset()
        {
            base.Reset();
            landing_stage = LandingStage.None;
            scanner.Reset();
            DecelerationTimer.Reset();
            dP_up_timer.Reset();
            dP_down_timer.Reset();
            dP_threshold = C.MaxDPressure;
            last_Err = 0;
            last_dP = 0;
            Working = false;
            scanned = false;
            flat_target = false;
            landing_trajectory = null;
            trajectory_renderer?.Reset();
            trajectory_renderer = null;
            Executor.Reset();
        }

        protected override bool check_target()
        {
            if(!base.check_target())
                return false;
            var orb = CFG.Target.GetOrbit();
            if(orb != null && orb.referenceBody != VSL.Body)
            {
                Status(Colors.Warning, "Target should be in the same sphere of influence.");
                return false;
            }
            if(!CFG.Target.IsProxy)
                return true;
            if(!CFG.Target.IsVessel)
            {
                Status(Colors.Warning, "Target should be a vessel or a waypoint");
                return false;
            }
            // ReSharper disable once InvertIf
            if(!TargetVessel.LandedOrSplashed)
            {
                Status(Colors.Warning, "Target vessel should be landed");
                return false;
            }
            return true;
        }

        protected override bool setup()
        {
            if(VSL.Engines.NoActiveEngines)
            {
                Status(Colors.Warning,
                    "No engines are active, unable to calculate trajectory.\n"
                    + "Please, activate ship's engines and try again.");
                return false;
            }
            if(!VSL.Engines.HaveThrusters)
            {
                Status(Colors.Warning,
                    "There are only Maneuver/Manual engines in current profile.\n" + "Please, change engines profile.");
                return false;
            }
            if(DiscontinuousOrbit(VesselOrbit))
            {
                Status(Colors.Warning,
                    "Ship's orbit is discontinuous.\n"
                    + "Cannot perform a targeted landing from unstable orbit.");
                return false;
            }
            VSL.OnPlanetParams.DragCurveK = AtmoSim.C.DragCurveK;
            return base.setup();
        }

        protected bool landing => landing_stage != LandingStage.None;

        protected bool check_initial_trajectory()
        {
            var fuel_needed = trajectory.GetTotalFuel();
            var hover_time = fuel_needed < VSL.Engines.AvailableFuelMass
                ? VSL.Engines.MaxHoverTimeASL(VSL.Engines.AvailableFuelMass - fuel_needed)
                : 0;
            var status = "";
            var needed_hover_time = LandASAP ? C.HoverTimeThreshold / 5 : C.HoverTimeThreshold;
            var enough_fuel = hover_time > needed_hover_time || CheatOptions.InfinitePropellant;
            if(trajectory.DistanceToTarget < C.Dtol && enough_fuel)
                return true;
            if(!enough_fuel)
            {
                status += "<b>WARNING</b>: Fuel is "
                          + Colors.Selected2.Tag("<b>{0:P0}</b>",
                              (needed_hover_time - hover_time) / needed_hover_time)
                          + "below safe margin for powered landing.\n";
                if(Body.atmosphere && VSL.OnPlanetParams.HaveParachutes)
                    status += "<i>Landing with parachutes may be possible, "
                              + "but you're advised to supervise the process.</i>\n";
            }
            if(trajectory.DistanceToTarget > C.Dtol)
                status += "<b>WARNING</b>: Predicted landing site is too far from the target.\nError is "
                          + Colors.Selected2.Tag("<b>{0}</b>\n",
                              Utils.formatBigValue((float)trajectory.DistanceToTarget, "m"));
            if(trajectory.WillOverheat)
                status += "<b>WARNING</b>: predicted reentry temperature is "
                          + Colors.Selected2.Tag("<b>{0:F0}K</b>\n", trajectory.MaxShipTemperature)
                          + Colors.Danger.Tag("<b>The ship may loose integrity and explode!</b>\n");
            status += Colors.Danger.Tag("\n<b>Push to proceed. At your own risk.</b>");
            Status(Colors.Warning, status);
            return false;
        }

        protected void update_landing_trajectory()
        {
            landing_trajectory =
                new LandingTrajectory(VSL, Vector3d.zero, VSL.Physics.UT, CFG.Target, TargetAltitude, true, true);
        }

        private double last_update = -1;

        private void update_landing_trajectory_each(double seconds)
        {
            if(!(VSL.Physics.UT - last_update > seconds))
                return;
            update_landing_trajectory();
            last_update = VSL.Physics.UT;
        }

        protected override bool trajectory_computed()
        {
            if(!base.trajectory_computed())
                return false;
            landing_trajectory = trajectory;
            return true;
        }

        protected bool coast_to_start()
        {
            VSL.Info.Countdown = landing_trajectory.BrakeStartPoint.UT - VSL.Physics.UT - ManeuverOffset;
            if(VSL.Info.Countdown > 0)
            {
                if(scan_for_landing_site_when_in_range())
                    return true;
                if(CFG.AP1[Autopilot1.Maneuver])
                {
                    if(drag_accel > ThrottleControl.C.MinDeltaV
                       || landing_trajectory.BrakeStartPoint.UT
                       - Math.Max(MAN.NodeUT, VSL.Physics.UT)
                       - VSL.Info.TTB
                       - VSL.Torque.NoEngines.RotationTime2Phase((float)Utils.Angle2(VesselOrbit.vel, MAN.NodeDeltaV))
                       < CorrectionOffset)
                    {
                        CFG.AP1.OffIfOn(Autopilot1.Maneuver);
                        clear_nodes();
                    }
                    else
                    {
                        Status("Correcting trajectory...");
                        return true;
                    }
                }
                Status("Coasting...");
                VSL.Controls.NoDewarpOffset = true;
                if(!correct_trajectory())
                {
                    if(VSL.vessel.dynamicPressurekPa <= 0)
                        return true;
                    update_trajectory();
                    brakes_on_if_requested();
                    CFG.AT.OnIfNot(Attitude.Custom);
                    ATC.SetThrustDirW(VSL.vessel.srf_vel_direction);
                    return true;
                }
            }
            CFG.AP1.OffIfOn(Autopilot1.Maneuver);
            start_landing();
            return false;
        }

        protected void start_landing()
        {
            Working = false;
            clear_nodes();
            update_trajectory();
            VSL.Controls.StopWarp();
            VSL.Controls.SetAttitudeError(180);
            CFG.AltitudeAboveTerrain = false;
            update_landing_trajectory();
            landing_stage = LandingStage.Wait;
            pressureASL = Body.GetPressure(0);
        }

        protected void warp_to_countdown()
        {
            if(VSL.Controls.CanWarp)
                VSL.Controls.WarpToTime =
                    VSL.Physics.UT + (VSL.Info.Countdown > 0 ? Utils.ClampH(VSL.Info.Countdown, 60) : 60);
            else
                VSL.Controls.StopWarp();
        }

        private float drag_accel => VSL.OnPlanetParams.Drag.magnitude / VSL.Physics.M;

        private bool correct_trajectory()
        {
            warp_to_countdown();
            if(!CorrectionTimer.TimePassed)
                return false;
            CorrectionTimer.Reset();
            if(drag_accel > ThrottleControl.C.MinDeltaV)
                return true;
            trajectory = new LandingTrajectory(VSL, Vector3d.zero, VSL.Physics.UT, CFG.Target, TargetAltitude);
            if(trajectory.DeltaR <= 0
               && Math.Abs(trajectory.DeltaFi) * Mathf.Deg2Rad * Body.Radius < C.Dtol)
                return !Body.atmosphere;
            fine_tune_approach();
            return false;
        }

        protected void add_correction_node_if_needed()
        {
            var nodeDeltaV = trajectory.NodeDeltaV;
            var dV_threshold = Utils.ClampL(C.CorrectionMinDv * (1 - CFG.Target.AngleTo(VSL) / Math.PI),
                ThrottleControl.C.MinDeltaV * 2);
            if(nodeDeltaV.magnitude > dV_threshold)
            {
                clear_nodes();
                add_node_rel(nodeDeltaV, trajectory.StartUT);
                CFG.AP1.OnIfNot(Autopilot1.Maneuver);
                VSL.Controls.StopWarp();
            }
            else
                update_landing_trajectory();
        }

        private double distance_from_ground(Orbit orb, double UT)
        {
            var pos = BodyRotationAtdT(Body, VSL.Physics.UT - UT) * orb.getRelativePositionAtUT(UT);
            return pos.magnitude - VSL.Geometry.D - Body.Radius - Body.TerrainAltitude(pos.xzy + Body.position);
        }

        private double obstacle_between(BaseTrajectory trj, double start, double stop, float offset)
        {
            var UT = start;
            var dT = (stop - start);
            double dist = 1;
            while(dT > 0.01)
            {
                var d1p = UT + dT > stop ? double.MaxValue : distance_from_ground(trj.Orbit, UT + dT);
                var d1m = UT - dT < start ? double.MaxValue : distance_from_ground(trj.Orbit, UT - dT);
                if(d1p < d1m)
                {
                    dist = d1p;
                    UT += dT;
                }
                else
                {
                    dist = d1m;
                    UT -= dT;
                }
                if(dist < offset)
                    return offset - dist;
                dT /= 2;
            }
            return offset - dist;
        }

        private double obstacle_ahead(float offset = 0)
        {
            if(trajectory != null)
            {
                obstacle_between(trajectory,
                    trajectory.StartUT,
                    Math.Min(trajectory.FlyAbovePoint.UT, trajectory.AtTargetUT - 0.1),
                    offset);
            }
            return -1;
        }

        private IEnumerator<double> obstacle_searcher;

        private IEnumerator<double> biggest_obstacle_searcher(LandingTrajectory trj, float offset)
        {
            var start = trj.StartUT;
            var stop = trj.BrakeEndPoint.UT;
            var dT = (stop - start) / 100;
            var UT0 = start;
            var UT1 = UT0 + dT;
            var dist = -1.0;
            while(UT0 < stop)
            {
                Status("Scanning for obstacles: " + Colors.Good.Tag("{0:P1}"),
                    Math.Min(1, (UT1 - start) / (stop - start)));
                var d = obstacle_between(trj, UT0, UT1, offset);
                UT0 = UT1;
                UT1 += dT;
                if(d > dist)
                    dist = d;
                yield return dist;
            }
            ClearStatus();
        }

        protected bool find_biggest_obstacle_ahead(float offset, out double obstacle_height)
        {
            obstacle_height = -1;
            if(trajectory == null)
                return false;
            if(obstacle_searcher == null)
            {
                obstacle_searcher = biggest_obstacle_searcher(trajectory, offset);
                if(!obstacle_searcher.MoveNext())
                    return false;
            }
            obstacle_height = obstacle_searcher.Current;
            if(obstacle_searcher.MoveNext())
                return true;
            obstacle_searcher = null;
            return false;
        }

        private void rel_altitude_if_needed()
        {
            CFG.AltitudeAboveTerrain = VSL.Altitude.Relative < 5000;
        }

        private void approach()
        {
            CFG.BR.Off();
            CFG.BlockThrottle = true;
            CFG.AltitudeAboveTerrain = true;
            CFG.VF.On(VFlight.AltitudeControl);
            CFG.DesiredAltitude = C.ApproachAlt < VSL.Altitude.Relative / 2
                ? C.ApproachAlt
                : Utils.ClampL(VSL.Altitude.Relative / 2, VSL.Geometry.H * 2);
            SetTarget(CFG.Target);
            CFG.Nav.On(Navigation.GoToTarget);
            if(CFG.Target.IsVessel)
                CFG.Target.Radius = 7;
            landing_stage = LandingStage.Approach;
        }

        private void decelerate(bool collision_detected)
        {
            VSL.Controls.StopWarp();
            DecelerationTimer.Reset();
            landing_stage = LandingStage.Decelerate;
            Working = collision_detected;
            if(collision_detected)
                landing_trajectory = null;
        }

        private void land()
        {
            if(CFG.Target && !CFG.Target.IsVessel)
                LND.StartFromTarget();
            CFG.AP1.On(Autopilot1.Land);
            landing_stage = LandingStage.Land;
        }

        private void compute_terminal_velocity()
        {
            terminal_velocity = 0;
            if(VSL.VerticalSpeed.Absolute > -100 || VSL.Altitude.Relative < 100 + VSL.Geometry.H)
            {
                terminal_velocity = Utils.ClampL(-VSL.VerticalSpeed.Absolute, 0.1f);
                VSL.Info.Countdown = (VSL.Altitude.Relative - VSL.Geometry.H) / terminal_velocity;
            }
            else
            {
                terminal_velocity = Math.Abs(Vector3d.Dot(trajectory.AtTargetVel, trajectory.AtTargetPos.normalized));
                VSL.Info.Countdown = trajectory.TimeToTarget;
            }
        }

        private void setup_for_deceleration()
        {
            CFG.VTOLAssistON = true;
            CFG.AltitudeAboveTerrain = true;
            CFG.AT.OnIfNot(Attitude.Custom);
            ATC.SetThrustDirW(VSL.vessel.srf_velocity);
        }

        protected override void update_trajectory(bool reset = false)
        {
            base.update_trajectory(reset);
            VSL.Info.CustomMarkersWP.Add(trajectory.SurfacePoint);
            update_drag_curve_K();
        }

        private double prev_deltaR = double.NaN;

        private void update_drag_curve_K()
        {
            if(trajectory != null
               && !double.IsNaN(prev_deltaR)
               && VSL.vessel.dynamicPressurekPa > 0
               && VSL.Engines.Thrust.IsZero())
            {
                var ddR = (float)((prev_deltaR - trajectory.DeltaR) * Body.Radius * Mathf.Deg2Rad);
                VSL.OnPlanetParams.ChangeDragCurveK(ddR * C.DragCurveF);
                prev_deltaR = double.NaN;
            }
            else
                prev_deltaR = trajectory.DeltaR;
        }

        private void nose_to_target()
        {
            CFG.BR.OnIfNot(BearingMode.Auto);
            BRC.ForwardDirection = Vector3d.Exclude(VSL.Physics.Up, CFG.Target.WorldPos(Body) - VSL.Physics.wCoM);
        }

        private bool correct_attitude_with_thrusters(float turn_time)
        {
            if(VSL.Engines.Active.Steering.Count > 0
               && (VSL.Controls.AttitudeError > Utils.ClampL(1 - rel_dP, 0.1f)
                   || VSL.Torque.NoEngines.MinStopTime() > turn_time)
               && (!VSL.Controls.HaveControlAuthority
                   || rel_dP > 0
                   || VSL.Torque.NoEngines.RotationTime2Phase(VSL.Controls.AttitudeError) > VSL.Info.Countdown))
            {
                THR.Throttle += (float)Utils.ClampH((1 + rel_dP)
                                                    * turn_time
                                                    / Utils.Clamp(VSL.Info.Countdown,
                                                        1,
                                                        AttitudeControlBase.C.MaxTimeToAlignment),
                    1);
                return true;
            }
            return false;
        }

        private Vector3d correction_direction()
        {
            var t0 = Utils.ClampL(VSL.Info.Countdown, 1e-5);
            var t1 = t0 * C.CorrectionTimeF;
            var TL = trajectory.SurfacePoint.WorldPos(Body) - CFG.Target.WorldPos(Body);
            var correction = -VSL.Physics.Up * VSL.Physics.G * (1 - t0 * t0 / t1 / t1)
                             + VSL.vessel.srf_velocity * 2 * ((t1 - t0) / t1 / t1);
            //overshot lies within [1; 2] interval
            correction += TL.ClampMagnitudeH(correction.magnitude)
                          / VSL.Engines.MaxAccel
                          * Utils.G0
                          * Body.GeeASL
                          * Utils.ClampH(1 + rel_dP, 2)
                          * Math.Pow(Utils.ClampH(trajectory.DistanceToTarget
                                                  / C.Dtol
                                                  * C.FlyOverAlt
                                                  / VSL.Altitude.Relative
                                                  * Utils.ClampL(
                                                      2
                                                      + Vector3.Dot(TL.normalized, VSL.HorizontalSpeed.normalized)
                                                      * C.CorrectionDirF,
                                                      1),
                                  1),
                              Anchor.C.DistanceCurve);
            return correction.normalized;
        }

        private bool correction_needed;

        private bool correct_landing_site()
        {
            ATC.SetThrustDirW(correction_direction());
            var rel_altitude = VSL.Altitude.Relative / C.FlyOverAlt;
            if(VSL.Controls.HaveControlAuthority
               && trajectory.DistanceToTarget > landing_deadzone
               && (correction_needed || rel_altitude < 1 || trajectory.DistanceToTarget > C.Dtol * rel_altitude))
            {
                THR.Throttle += Utils.ClampH(
                    (float)trajectory.DistanceToTarget / VSL.Engines.MaxAccel / C.Dtol * C.CorrectionThrustF,
                    VSL.OnPlanetParams.GeeVSF * 0.9f);
                correction_needed = trajectory.DistanceToTarget > C.Dtol;
                return true;
            }
            correction_needed = false;
            return false;
        }

        private void correct_landing_site_with_lift()
        {
            if(VSL.OnPlanetParams.Lift.IsZero())
                return;
            var LT = VSL.LocalDir(Vector3d.Exclude(VSL.Physics.Up,
                CFG.Target.WorldPos(Body) - trajectory.SurfacePoint.WorldPos(Body)));
            var lift = Vector3d.Exclude(VSL.Physics.UpL, VSL.OnPlanetParams.Lift);
            if(lift.magnitude / VSL.Physics.M > 0.1)
                ATC.SetCustomRotation(lift, LT);
        }

        public static Vector3d CorrectedBrakeVelocity(
            VesselWrapper VSL,
            Vector3d obt_vel,
            Vector3d obt_pos,
            double rel_dP,
            double countdown
        )
        {
            var vV = Vector3d.Project(obt_vel, obt_pos);
            var srfVel = Vector3d.Cross(VSL.Body.zUpAngularVelocity, obt_pos);
            var vBrake = VSL.Engines.AntigravTTB((float)vV.magnitude);
            var vFactor = 0.5 * (VSL.Body.atmDensityASL + rel_dP) + vBrake / Utils.ClampL(countdown, 0.1f);
            return obt_vel - vV * (1 - Utils.Clamp(vFactor, 0.1, 1)) + srfVel;
        }

        private Vector3d corrected_brake_velocity(Vector3d obt_vel, Vector3d obt_pos)
        {
            return CorrectedBrakeVelocity(VSL, obt_vel, obt_pos, rel_dP, VSL.Info.Countdown).xzy;
        }

        private Vector3d corrected_brake_direction(Vector3d vel, Vector3d pos)
        {
            var targetWorldPos = CFG.Target.WorldPos(Body);
            return QuaternionD.AngleAxis(Utils.ProjectionAngle(Vector3d.Exclude(pos, vel),
                           trajectory.SurfacePoint.WorldPos(Body) - targetWorldPos,
                           Vector3d.Cross(pos, vel)),
                       VSL.Physics.Up)
                   * vel;
        }

        private void set_destination_vector()
        {
            VSL.Info.Destination = CFG.Target.WorldPos(Body) - VSL.Physics.wCoM;
        }

        private void scan_for_landing_site()
        {
            if(scanned)
                return;
            if(scanner.Idle)
            {
                scanner.Start(CFG.Target.Pos, C.PointsPerFrame, 0.01);
                scanner.MaxDist = CorrectionMaxDist.Value * 1000;
            }
            Status("Scanning for {0} surface to land: {1}",
                Colors.Active.Tag("<b>flat</b>"),
                Colors.Good.Tag(scanner.Progress.ToString("P1")));
            if(scanner.Scan())
                return;
            flat_target = scanner.FlatRegion != null
                          && (!scanner.FlatRegion.Equals(CFG.Target.Pos) || !CFG.Target.IsVessel);
            if(flat_target)
            {
                if(!scanner.FlatRegion.Equals(CFG.Target.Pos))
                {
                    SetTarget(new WayPoint(scanner.FlatRegion));
                    update_trajectory(true);
                    update_landing_trajectory();
                    Utils.Message(scanner.BestUnevenness < AutoLander.C.MaxUnevenness
                        ? "Found flat region for landing."
                        : "Moved landing site to a flatter region.");
                }
            }
            scanned = true;
        }

        private bool scan_for_landing_site_when_in_range()
        {
            if(!CorrectTarget
               || scanned
               || !(Utils.Angle2((Vector3)VSL.orbit.vel.xzy, -CFG.Target.VectorTo(VSL)) < C.ScanningAngle))
                return false;
            VSL.Controls.StopWarp();
            scan_for_landing_site();
            if(!scanned)
                return scanned;
            CFG.AP1.OffIfOn(Autopilot1.Maneuver);
            clear_nodes();
            fine_tune_approach();
            return scanned;
        }

        private void brakes_on_if_requested()
        {
            if(UseBrakes && VSL.vessel.staticPressurekPa > 0)
                VSL.BrakesOn();
        }

        private void do_aerobraking_if_requested(bool full = false)
        {
            if(!(VSL.vessel.staticPressurekPa > 0))
                return;
            if(UseBrakes)
                VSL.BrakesOn();
            if(UseChutes
               && VSL.OnPlanetParams.HaveUsableParachutes
               && (full || !VSL.OnPlanetParams.ParachutesActive))
                VSL.OnPlanetParams.ActivateParachutesASAP();
        }

        private void stop_aerobraking()
        {
            if(UseBrakes)
                VSL.BrakesOn(false);
            if(UseChutes && VSL.OnPlanetParams.ParachutesActive)
                VSL.OnPlanetParams.CutActiveParachutes();
        }

        private void brake_with_drag()
        {
            var dir = VSL.OnPlanetParams.MaxAeroForceL;
            if(dir.IsZero())
            {
                dir = VSL.Geometry.MaxAreaDirection;
                dir = Mathf.Sign(Vector3.Dot(dir, VSL.vessel.srf_velocity)) * dir;
            }
            else
                dir = -VSL.WorldDir(dir);
            ATC.SetCustomRotationW(dir, VSL.vessel.srf_velocity);
        }

        private bool is_overheating()
        {
            return rel_dP > 0
                   && VSL.vessel.Parts.Any(p =>
                       p.temperature / p.maxTemp > PhysicsGlobals.TemperatureGaugeThreshold
                       || p.skinTemperature / p.skinMaxTemp > PhysicsGlobals.TemperatureGaugeThreshold);
        }

        protected bool do_land()
        {
            if(VSL.LandedOrSplashed)
            {
#if DEBUG
                if(CFG.Target)
                    Log("Distance to target: {}", CFG.Target.DistanceTo(VSL));
#endif
                stop_aerobraking();
                THR.Throttle = 0;
                SetTarget();
                ClearStatus();
                Disable();
                return true;
            }
            update_trajectory();
            VSL.Engines.ActivateEngines();
            NoEnginesTimer.RunIf(VSL.Engines.MaxThrustM.Equals(0) && !VSL.Engines.HaveNextStageEngines);
            landing_deadzone = VSL.Geometry.D + CFG.Target.AbsRadius;
            if(VSL.vessel.dynamicPressurekPa > 0)
            {
                if(!dP_up_timer.RunIf(VSL.Controls.AttitudeError > last_Err
                                      || Mathf.Abs(VSL.Controls.AttitudeError - last_Err) < 0.01f))
                    dP_down_timer.RunIf(
                        VSL.Controls.AttitudeError < last_Err && VSL.vessel.dynamicPressurekPa < last_dP);
            }
            else
                dP_threshold = C.MaxDPressure;
            rel_dP = VSL.vessel.dynamicPressurekPa / dP_threshold;
            last_Err = VSL.Controls.AttitudeError;
            float rel_Ve;
            Vector3d brake_pos, brake_vel, vector_from_target;
            vector_from_target = CFG.Target.VectorTo(VSL.vessel);
            vessel_within_range = CFG.Target.DistanceTo(VSL.vessel) < C.Dtol;
            vessel_after_target = Vector3.Dot(VSL.HorizontalSpeed.Vector, vector_from_target) >= 0;
            target_within_range = trajectory.DistanceToTarget < C.Dtol;
            landing_before_target = trajectory.DeltaR > 0;
            compute_terminal_velocity();
            switch(landing_stage)
            {
                case LandingStage.Wait:
                    Status("Preparing for deceleration...");
                    THR.Throttle = 0;
                    nose_to_target();
                    rel_altitude_if_needed();
                    brakes_on_if_requested();
                    update_landing_trajectory_each(5);
                    var obt_vel = VesselOrbit.getOrbitalVelocityAtUT(landing_trajectory.BrakeStartPoint.UT);
                    brake_pos = VesselOrbit.getRelativePositionAtUT(landing_trajectory.BrakeStartPoint.UT);
                    brake_vel = corrected_brake_velocity(obt_vel, brake_pos);
                    brake_pos = brake_pos.xzy;
                    brake_vel = corrected_brake_direction(brake_vel, brake_pos);
                    CFG.AT.OnIfNot(Attitude.Custom);
                    ATC.SetThrustDirW(brake_vel);
                    lateral_angle.Value = lateral_angle.Upper
                                          + lateral_angle.Lower
                                          - Utils.Angle2(Vector3d.Exclude(brake_pos, brake_vel),
                                              Vector3d.Exclude(brake_pos, -vector_from_target));
                    if(lateral_angle)
                        THR.Throttle = (float)Math.Min((lateral_angle.Upper + lateral_angle.Lower - lateral_angle) / 90,
                            1);
                    VSL.Info.TTB = landing_trajectory.BrakeDuration;
                    VSL.Info.Countdown = landing_trajectory.BrakeStartPoint.UT - VSL.Physics.UT - 1;
                    VSL.Info.Countdown += landing_trajectory.DeltaR * Body.Radius * Mathf.Deg2Rad / VSL.HorizontalSpeed;
                    correct_attitude_with_thrusters(
                        VSL.Torque.MaxPossible.RotationTime2Phase(VSL.Controls.AttitudeError));
                    if(obstacle_ahead() > 0)
                    {
                        decelerate(true);
                        break;
                    }
                    if(VSL.Info.Countdown <= rel_dP || is_overheating())
                    {
                        decelerate(false);
                        break;
                    }
                    if(VSL.Controls.CanWarp && (!CorrectTarget || VSL.Info.Countdown > CorrectionOffset))
                        VSL.Controls.WarpToTime = VSL.Physics.UT + VSL.Info.Countdown;
                    else
                        VSL.Controls.StopWarp();
                    if(CorrectTarget && VSL.Info.Countdown < CorrectionOffset)
                        scan_for_landing_site();
                    break;
                case LandingStage.Decelerate:
                    rel_altitude_if_needed();
                    CFG.BR.Off();
                    if(Working)
                    {
                        Status(Colors.Danger, "Possible collision detected.");
                        correct_attitude_with_thrusters(
                            VSL.Torque.MaxPossible.RotationTime2Phase(VSL.Controls.AttitudeError));
                        Executor.Execute(VSL.Physics.Up * 10);
                        if(obstacle_ahead(100) > 0)
                        {
                            CollisionTimer.Reset();
                            break;
                        }
                        if(!CollisionTimer.TimePassed)
                            break;
                        start_landing();
                        break;
                    }
                    Status("Decelerating. Landing site error: {0}",
                        Utils.formatBigValue((float)trajectory.DistanceToTarget, "m"));
                    if(CorrectTarget)
                        scan_for_landing_site();
                    do_aerobraking_if_requested();
                    var overheating = is_overheating();
                    if(!overheating && VSL.Engines.AvailableFuelMass / VSL.Engines.MaxMassFlow < C.LandingThrustTime)
                    {
                        Message(10, "Not enough fuel for powered landing.\nPerforming emergency landing...");
                        landing_stage = LandingStage.HardLanding;
                        break;
                    }
                    if(VSL.Controls.HaveControlAuthority)
                        DecelerationTimer.Reset();
                    if(vessel_after_target)
                    {
                        if(Executor.Execute(-VSL.vessel.srf_velocity, C.BrakeEndSpeed))
                            break;
                    }
                    else if(overheating
                            || !landing_before_target
                            && !DecelerationTimer.TimePassed
                            && trajectory.DistanceToTarget > landing_deadzone)
                    {
                        THR.Throttle = 0;
                        VSL.Info.TTB = VSL.Engines.TTB((float)VSL.vessel.srfSpeed);
                        CFG.AT.OnIfNot(Attitude.Custom);
                        var aerobraking = rel_dP > 0 && VSL.OnPlanetParams.ParachutesActive;
                        if(overheating)
                        {
                            ATC.SetThrustDirW(VSL.vessel.srf_velocity);
                            THR.Throttle = 1;
                        }
                        else
                        {
                            brake_vel = corrected_brake_velocity(VesselOrbit.vel, VesselOrbit.pos);
                            brake_vel = corrected_brake_direction(brake_vel, VesselOrbit.pos.xzy);
                            ATC.SetThrustDirW(brake_vel);
                            THR.Throttle = CFG.Target.DistanceTo(VSL.vessel) > trajectory.DistanceToTarget
                                ? (float)Utils.ClampH(trajectory.DistanceToTarget
                                                      / landing_deadzone
                                                      / 3
                                                      / (1 + Vector3.Dot(brake_vel.normalized, VSL.Physics.Up)),
                                    1)
                                : 1;
                        }
                        if(THR.Throttle > 0 || aerobraking)
                            break;
                    }
                    landing_stage = LandingStage.Coast;
                    landing_trajectory = null;
                    break;
                case LandingStage.Coast:
                    Status("Coasting. Landing site error: {0}",
                        Utils.formatBigValue((float)trajectory.DistanceToTarget, "m"));
                    if(is_overheating())
                    {
                        decelerate(false);
                        break;
                    }
                    THR.Throttle = 0;
                    nose_to_target();
                    setup_for_deceleration();
                    if(landing_before_target)
                    {
                        if(VSL.vessel.mach < 1)
                            stop_aerobraking();
                    }
                    else
                    {
                        brakes_on_if_requested();
                        if(trajectory.DistanceToTarget * 2 > CFG.Target.DistanceTo(VSL.vessel))
                        {
                            decelerate(false);
                            break;
                        }
                    }
                    if(correct_landing_site())
                        correct_attitude_with_thrusters(
                            VSL.Torque.MaxPossible.RotationTime2Phase(VSL.Controls.AttitudeError));
                    VSL.Info.TTB = VSL.Engines.TTB((float)VSL.vessel.srfSpeed);
                    VSL.Info.Countdown -=
                        Math.Max(VSL.Info.TTB + VSL.Torque.NoEngines.TurnTime + VSL.vessel.dynamicPressurekPa,
                            ManeuverOffset);
                    if(VSL.Info.Countdown > 0 && !vessel_after_target)
                    {
                        if(THR.Throttle.Equals(0))
                            warp_to_countdown();
                    }
                    else if(!landing_before_target && !target_within_range)
                        decelerate(false);
                    else
                    {
                        Working = false;
                        rel_Ve = VSL.Engines.RelVeASL;
                        if(rel_Ve <= 0)
                        {
                            Message(10, "Not enough thrust for powered landing.\nPerforming emergency landing...");
                            landing_stage = LandingStage.HardLanding;
                            break;
                        }
                        if(!VSL.Controls.HaveControlAuthority
                           && !VSL.Torque.HavePotentialControlAuthority
                           && Utils.Angle2(VSL.Engines.CurrentDefThrustDir, (Vector3)VSL.vessel.srf_velocity) > 45)
                        {
                            Message(10, "Lacking control authority to land properly.\nPerforming emergency landing...");
                            landing_stage = LandingStage.HardLanding;
                            break;
                        }
                        var fuel_left = VSL.Engines.AvailableFuelMass;
                        var fuel_needed = VSL.Engines.FuelNeeded((float)terminal_velocity, rel_Ve);
                        var needed_hover_time = LandASAP ? C.HoverTimeThreshold / 5 : C.HoverTimeThreshold;
                        if(!CheatOptions.InfinitePropellant
                           && (fuel_needed >= fuel_left
                               || VSL.Engines.MaxHoverTimeASL(fuel_left - fuel_needed) < needed_hover_time))
                        {
                            Message(10, "Not enough fuel for powered landing.\nPerforming emergency landing...");
                            landing_stage = LandingStage.HardLanding;
                            break;
                        }
                        landing_stage = LandingStage.SoftLanding;
                    }
                    break;
                case LandingStage.HardLanding:
                    var status = VSL.OnPlanetParams.ParachutesActive
                        ? "<b>Landing on parachutes.</b>"
                        : "<b>Emergency Landing.</b>";
                    status = Colors.Warning.Tag(status);
                    status += string.Format("\nVertical impact speed: <b>{0}</b>",
                        Colors.Danger.Tag(Utils.formatBigValue((float)terminal_velocity, "m/s")));
                    set_destination_vector();
                    CFG.BR.Off();
                    THR.Throttle = 0;
                    var not_too_hot = VSL.vessel.externalTemperature < VSL.Physics.MinMaxTemperature;
                    if(not_too_hot)
                        setup_for_deceleration();
                    if(VSL.Engines.MaxThrustM > 0
                       && terminal_velocity > 4
                       && (VSL.Controls.HaveControlAuthority || VSL.Torque.HavePotentialControlAuthority))
                    {
                        VSL.Info.TTB = VSL.Engines.OnPlanetTTB(VSL.vessel.srf_velocity,
                            VSL.Physics.Up,
                            VSL.Altitude.Absolute);
                        VSL.Info.Countdown -= VSL.Info.TTB;
                        if(!target_within_range
                           && VSL.vessel.mach < 1
                           && VSL.Info.Countdown > VSL.Torque.NoEngines.TurnTime)
                            correct_landing_site_with_lift();
                        if((VSL.Info.Countdown < 0
                            && (!VSL.OnPlanetParams.HaveParachutes
                                || VSL.OnPlanetParams.ParachutesActive && VSL.OnPlanetParams.ParachutesDeployed)))
                            Working = true;
                        else
                            Working &= VSL.Info.Countdown <= 0.5f;
                        if(Working)
                        {
                            THR.CorrectThrottle = false;
                            THR.Throttle = VSL.VerticalSpeed.Absolute < -5 ? 1 : VSL.OnPlanetParams.GeeVSF;
                        }
                        status += "\nWill deceletate as much as possible before impact.";
                    }
                    if(Body.atmosphere && VSL.OnPlanetParams.HaveUsableParachutes)
                    {
                        if(vessel_within_range || vessel_after_target || !landing_before_target)
                            VSL.OnPlanetParams.ActivateParachutesASAP();
                        else
                            VSL.OnPlanetParams.ActivateParachutesBeforeUnsafe();
                        if(!VSL.OnPlanetParams.ParachutesActive)
                        {
                            //don't push our luck when it's too hot outside
                            if(not_too_hot)
                            {
                                if(target_within_range || VSL.vessel.mach > 1)
                                    brake_with_drag();
                                else
                                    correct_landing_site_with_lift();
                            }
                            else
                            {
                                CFG.AT.Off();
                                CFG.StabilizeFlight = false;
                                VSL.vessel.ActionGroups.SetGroup(KSPActionGroup.SAS, false);
                            }
                            StageTimer.RunIf(Body.atmosphere
                                             && //!VSL.Controls.HaveControlAuthority &&
                                             VSL.vessel.currentStage - 1 > VSL.OnPlanetParams.NearestParachuteStage
                                             && VSL.vessel.dynamicPressurekPa > C.DropBallastThreshold * pressureASL
                                             && VSL.vessel.mach > C.MachThreshold);
                            if(CFG.AutoParachutes)
                                status += "\nWaiting for the right moment to deploy parachutes.";
                            else
                                status += Colors.Danger
                                    .Tag("\nAutomatic parachute deployment is disabled."
                                         + "\nActivate parachutes manually when needed.");
                        }
                    }
                    if(Body.atmosphere)
                        VSL.BrakesOn();
                    if(!VSL.OnPlanetParams.HaveParachutes
                       && !VSL.Engines.HaveNextStageEngines
                       && (VSL.Engines.MaxThrustM.Equals(0) || !VSL.Controls.HaveControlAuthority))
                    {
                        if(Body.atmosphere && not_too_hot)
                            brake_with_drag();
                        status += Colors.Danger.Tag("\n<b>Crash is imminent!</b>");
                    }
                    Status(status);
                    break;
                case LandingStage.SoftLanding:
                    CFG.BR.Off();
                    THR.Throttle = 0;
                    set_destination_vector();
                    setup_for_deceleration();
                    if(vessel_within_range || vessel_after_target)
                        do_aerobraking_if_requested(true);
                    else
                        brakes_on_if_requested();
                    var turn_time = VSL.Torque.MaxPossible.RotationTime2Phase(VSL.Controls.AttitudeError);
                    var CPS_Correction = CPS.CourseCorrection;
                    if(!CPS_Correction.IsZero())
                    {
                        Status(Colors.Danger, "Avoiding collision!");
                        CFG.Target = trajectory.SurfacePoint;
                        trajectory.Target = CFG.Target;
                        trajectory.TargetAltitude = CFG.Target.Pos.Alt;
                        ATC.SetThrustDirW(CPS_Correction - VSL.vessel.srf_velocity);
                        THR.DeltaV = CPS_Correction.magnitude + (float)VSL.vessel.srfSpeed;
                        THR.CorrectThrottle = false;
                        flat_target = false;
                        break;
                    }
                    if(!Working)
                    {
                        correct_landing_site();
                        correct_attitude_with_thrusters(turn_time);
                        VSL.Info.TTB = VSL.Engines.OnPlanetTTB(VSL.vessel.srf_velocity,
                            VSL.Physics.Up,
                            VSL.Altitude.Absolute);
                        VSL.Info.Countdown -= VSL.Info.TTB + turn_time;
                        Working = VSL.Info.Countdown <= 0 || VSL.vessel.srfSpeed < C.BrakeEndSpeed;
                        if(!Working)
                        {
                            if(VSL.Controls.InvAlignmentFactor > 0.5)
                                Status("Final deceleration: correcting attitude.\nLanding site error: {0}",
                                    Utils.formatBigValue((float)trajectory.DistanceToTarget, "m"));
                            else
                                Status("Final deceleration: waiting for the burn.\nLanding site error: {0}",
                                    Utils.formatBigValue((float)trajectory.DistanceToTarget, "m"));
                            break;
                        }
                    }
                    if(Working)
                    {
                        ATC.SetThrustDirW(correction_direction());
                        if(!VSL.Controls.HaveControlAuthority)
                        {
                            correct_attitude_with_thrusters(turn_time);
                            if(!VSL.Torque.HavePotentialControlAuthority)
                                landing_stage = LandingStage.HardLanding;
                            break;
                        }
                        THR.CorrectThrottle = false;
                        VSL.Info.TTB = VSL.Engines.OnPlanetTTB(VSL.vessel.srf_velocity,
                            VSL.Physics.Up,
                            VSL.Altitude.Absolute);
                        VSL.Info.Countdown -= VSL.Info.TTB + turn_time;
                        if(target_within_range && flat_target || VSL.Altitude.Relative > AutoLander.C.WideCheckAltitude)
                        {
                            if(VSL.VerticalSpeed.Absolute < 0)
                                THR.Throttle = VSL.Info.Countdown < 0.1f
                                    ? 1
                                    : Utils.Clamp(-VSL.VerticalSpeed.Absolute
                                                  / (VSL.Engines.MaxAccel - VSL.Physics.G)
                                                  / Utils.ClampL((float)VSL.Info.Countdown, 0.01f),
                                        VSL.OnPlanetParams.GeeVSF * 1.1f,
                                        1);
                            else
                                THR.Throttle =
                                    Utils.ClampH(VSL.HorizontalSpeed.Absolute
                                                 / C.BrakeThrustThreshold
                                                 * VSL.Controls.AlignmentFactor,
                                        1);
                        }
                        else
                            THR.Throttle = 1;
                        if(VSL.Altitude.Relative > AutoLander.C.StopAtH * VSL.Geometry.D
                           && VSL.VerticalSpeed.Absolute < 0)
                        {
                            Working = THR.Throttle > 0.7 || VSL.Info.Countdown < 10;
                            Status("Final deceleration. Landing site error: {0}",
                                Utils.formatBigValue((float)trajectory.DistanceToTarget, "m"));
                            break;
                        }
                    }
                    THR.Throttle = 0;
                    if(LandASAP)
                        landing_stage = LandingStage.LandHere;
                    else
                    {
                        stop_aerobraking();
                        if(CFG.Target.DistanceTo(VSL.vessel) - VSL.Geometry.R > C.Dtol)
                            approach();
                        else
                            land();
                    }
                    break;
                case LandingStage.LandHere:
                    Status(Colors.Good, "Landing...");
                    CFG.BR.Off();
                    CFG.BlockThrottle = true;
                    CFG.AltitudeAboveTerrain = true;
                    CFG.VF.On(VFlight.AltitudeControl);
                    CFG.HF.OnIfNot(HFlight.Stop);
                    if(CFG.DesiredAltitude >= 0 && !VSL.HorizontalSpeed.MoovingFast)
                        CFG.DesiredAltitude = 0;
                    else
                        CFG.DesiredAltitude = Utils.ClampL(VSL.Altitude.Relative / 2, VSL.Geometry.H * 2);
                    break;
                case LandingStage.Approach:
                    Status("Approaching the target...");
                    set_destination_vector();
                    if(VSL.Engines.AvailableFuelMass / VSL.Engines.MaxMassFlow < C.LandingThrustTime)
                    {
                        CFG.Nav.Off();
                        landing_stage = LandingStage.LandHere;
                        break;
                    }
                    if(!CFG.Nav[Navigation.GoToTarget])
                        land();
                    break;
                case LandingStage.Land:
                    set_destination_vector();
                    break;
            }
            return false;
        }

        protected abstract Autopilot2 program { get; }
        protected abstract string program_name { get; }

        public void DrawOptions()
        {
            GUILayout.BeginVertical();
            GUILayout.BeginHorizontal();
            Utils.ButtonSwitch("Use Brakes", ref UseBrakes, "Use brakes during deceleration.");
            if(Body.atmosphere && VSL.OnPlanetParams.HaveParachutes)
                Utils.ButtonSwitch("Use Parachutes", ref UseChutes, "Use parachutes during deceleration.");
            else
                GUILayout.Label("Use Parachutes", Styles.inactive_button);
            Utils.ButtonSwitch("Correct Target",
                ref CorrectTarget,
                "Search for a flat surface before deceleration and correct the target site.");
            Utils.ButtonSwitch("Land ASAP",
                ref LandASAP,
                "Do not try to Go To the target if missed or to search for a landing site near the surface.");
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label(new GUIContent("Max. Correction:",
                "Maximum distance of a corrected landing site from the original one"));
            CorrectionMaxDist.Draw("km", 1f, "F1", suffix_width: 25);
            GUILayout.FlexibleSpace();
            if(CFG.AP2[program])
            {
                if(GUILayout.Button(new GUIContent("Abort", "Abort " + program_name),
                    Styles.danger_button,
                    GUILayout.Width(60)))
                    CFG.AP2.XOff();
            }
            else
            {
                if(GUILayout.Button(new GUIContent("Start", "Start " + program_name),
                    Styles.enabled_button,
                    GUILayout.Width(60)))
                {
                    if(UseBrakes && Body.atmosphere)
                        VSL.Geometry.MeasureAreaWithBrakesAndRun(() =>
                            VSL.Engines.ActivateEnginesAndRun(() => CFG.AP2.XOn(program)));
                    else
                    {
                        VSL.Geometry.ResetAreaWithBrakes();
                        VSL.Engines.ActivateEnginesAndRun(() => CFG.AP2.XOn(program));
                    }
                }
            }
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
        }

        public void DrawTrajectory()
        {
            if(Time.timeScale <= 0 || FlightDriver.Pause)
                return;
            var trj = landing_trajectory ?? trajectory;
#if DEBUG
                DrawDebug();
                if(trj == null)
                    trj = current_landing_trajectory;
#endif
            if(trj != null)
            {
                if(trajectory_renderer == null)
                    trajectory_renderer = new TrajectoryRenderer(trj);
                else
                    trajectory_renderer.UpdateTrajectory(trj);
                trajectory_renderer.Draw();
            }
            else
                trajectory_renderer?.Deactivate();
        }


#if DEBUG
        void log_flight()
        {
            var v = VSL.vessel;
            CSV(
                VSL.Altitude.Absolute,
                v.staticPressurekPa,
                v.atmDensity,
                v.atmDensity / Body.atmDensityASL,
                v.atmosphericTemperature,
                VSL.Physics.G,
                v.srfSpeed,
                VSL.HorizontalSpeed.Absolute,
                Mathf.Abs(VSL.VerticalSpeed.Absolute),
                v.mach,
                v.dynamicPressurekPa,
                VSL.Controls.AttitudeError
            );
        }

        protected virtual void DrawDebug()
        {
            if(VSL == null || VSL.vessel == null || VSL.refT == null)
                return;
            if(IsActive)
            {
                Utils.GLVec(VSL.refT.position, VSL.vessel.srf_velocity, Color.yellow);
                if(CFG.Target)
                    Utils.GLLine(VSL.refT.position, CFG.Target.WorldPos(Body), Color.magenta);
            }
            if(landing_trajectory != null)
            {
                VSL.Info.AddCustopWaypoint(landing_trajectory.BrakeStartPoint.CBRelativePosInWorldFrame(), "Brake Start");
                VSL.Info.AddCustopWaypoint(landing_trajectory.BrakeEndPoint.CBRelativePosInWorldFrame(), "Brake End");
            }
        }
#endif
    }

    public abstract class PQS_Scanner
    {
        protected readonly VesselWrapper VSL;

        private Coordinates start;
        protected int points_per_frame;
        protected double delta, half;

        public double MaxDist = -1;
        protected double MaxUnevenness;
        public double BestUnevenness { get; protected set; }
        public Coordinates FlatRegion { get; protected set; }

        protected PQS_Scanner(VesselWrapper vsl, double max_unevenness)
        {
            VSL = vsl;
            MaxUnevenness = max_unevenness;
        }

        public virtual void Reset()
        {
            FlatRegion = null;
            BestUnevenness = double.MaxValue;
        }

        protected void Start(Coordinates start, int points_per_frame)
        {
            Reset();
            this.start = start.Copy();
            this.points_per_frame = points_per_frame;
            delta = VSL.Geometry.D / VSL.Body.Radius * Mathf.Rad2Deg;
            half = delta / 2;
        }

        private double altitude_delta(double lat, double lon, double prev_alt)
        {
            return Math.Abs(new Coordinates(lat, lon, 0).SurfaceAlt(VSL.Body, true) - prev_alt);
        }

        protected double calculate_unevenness(double lat, double lon)
        {
            var current_point = Coordinates.SurfacePoint(lat, lon, VSL.Body);
#if DEBUG
            VSL.Info.AddCustopWaypoint(current_point, "Checking...");
#endif
            if(current_point.OnWater)
                return double.PositiveInfinity;
            var alt_delta = altitude_delta(current_point.Lat - half, current_point.Lon - half, current_point.Alt);
            alt_delta += altitude_delta(current_point.Lat + half, current_point.Lon - half, current_point.Alt);
            alt_delta += altitude_delta(current_point.Lat + half, current_point.Lon + half, current_point.Alt);
            alt_delta += altitude_delta(current_point.Lat - half, current_point.Lon + half, current_point.Alt);
            return alt_delta / VSL.Geometry.D;
        }

        protected bool good_point(double lat, double lon, double unevenness)
        {
            return MaxDist < 0 || start.DistanceTo(new Coordinates(lat, lon, 0), VSL.Body) < MaxDist;
        }
    }

    public class PQS_Scanner_CDOS : PQS_Scanner
    {
        private CDOS_Optimizer2D_Generic optimizer;
        private IEnumerator optimization;

        public bool Idle => optimizer == null;

        public float Progress =>
            optimizer == null ? 0 : (float)Math.Min(MaxUnevenness / optimizer.BestValue, 1);

        public PQS_Scanner_CDOS(VesselWrapper vsl, double max_unevenness)
            : base(vsl, max_unevenness) { }

        public void Start(Coordinates pos, int num_points_per_frame, double tol)
        {
            Start(pos, num_points_per_frame);
            optimizer = new CDOS_Optimizer2D_Generic(pos.Lat,
                pos.Lon,
                delta * 10,
                tol * delta,
                1e-7,
                calculate_unevenness,
                good_point);
        }

        public override void Reset()
        {
            base.Reset();
            optimizer = null;
            optimization = null;
        }

        public bool Scan()
        {
            if(optimizer == null)
                return false;
            if(optimization == null)
                optimization = optimizer.GetEnumerator();
            for(var p = 0; p < points_per_frame; p++)
            {
                if(optimization.MoveNext())
                    continue;
                var best = optimizer.Best;
                FlatRegion = Coordinates.SurfacePoint(best.x, best.y, VSL.Body);
                BestUnevenness = best.z;
                return false;
            }
            return true;
        }
    }

    public class TrajectoryRenderer
    {
        private LandingTrajectory trajectory;
        private UnityLineRenderer path;
        private UnityLineRenderer after_brake_path;

        public TrajectoryRenderer(LandingTrajectory t)
        {
            UpdateTrajectory(t);
        }

        public void UpdateTrajectory(LandingTrajectory t)
        {
            trajectory = t;
            update();
        }

        private void update()
        {
            if(trajectory != null)
            {
                if(path == null && trajectory.Path)
                    path = new UnityLineRenderer("Landing path",
                        8,
                        31,
                        material: MapView.OrbitLinesMaterial);
                else if(path != null && !trajectory.Path)
                {
                    path.Reset();
                    path = null;
                }
                if(after_brake_path == null && trajectory.AfterBrakePath)
                    after_brake_path = new UnityLineRenderer("After brake path",
                        6,
                        31,
                        material: MapView.OrbitLinesMaterial);
                else if(after_brake_path != null && !trajectory.AfterBrakePath)
                {
                    after_brake_path.Reset();
                    after_brake_path = null;
                }
            }
            else
                Reset();
        }

        public void Draw()
        {
            if(MapView.MapIsEnabled)
            {
                update();
                if(path != null)
                    path.SetPoints(trajectory.Path
                            .CBRelativePathInWorldFrame(),
                        trajectory.Path.TemperatureMap());
                if(after_brake_path != null)
                    after_brake_path.SetPoints(trajectory.AfterBrakePath
                            .CBRelativePathInWorldFrame(),
                        Colors.Good);
            }
            else
                Deactivate();
        }

        public void Deactivate()
        {
            if(path != null)
                path.isActive = false;
            if(after_brake_path != null)
                after_brake_path.isActive = false;
        }

        public void Reset()
        {
            Deactivate();
            path?.Reset();
            after_brake_path?.Reset();
            path = after_brake_path = null;
        }
    }
}
