namespace DesktopBuddy.ModelCore;

/// <summary>Small, bounded, interruptible springs. No geometry or frame allocations.</summary>
public sealed class CompanionMotion
{
    public double Yaw { get; private set; }
    public double Roll { get; private set; }
    public double Scale { get; private set; } = 1;
    public double Lift { get; private set; }
    private double _yawV, _rollV, _scaleV, _liftV;
    public double TargetYaw { get; set; }
    public double TargetRoll { get; set; }
    public double TargetScale { get; set; } = 1;
    public double TargetLift { get; set; }
    public bool Step(double seconds)
    {
        // Substeps prevent frame stalls from injecting energy into the spring.
        var delta = double.IsFinite(seconds) ? Math.Clamp(seconds, 0, .08) : 0;
        while (delta > 0)
        {
            var dt = Math.Min(delta, 1d / 120); delta -= dt;
            Yaw = Integrate(Yaw, ref _yawV, Safe(TargetYaw, -22, 22, 0), dt);
            Roll = Integrate(Roll, ref _rollV, Safe(TargetRoll, -9, 9, 0), dt);
            Scale = Integrate(Scale, ref _scaleV, Safe(TargetScale, .96, 1.02, 1), dt);
            Lift = Integrate(Lift, ref _liftV, Safe(TargetLift, -.012, .025, 0), dt);
        }
        return Math.Abs(Yaw-Safe(TargetYaw,-22,22,0))>.015 || Math.Abs(Roll-Safe(TargetRoll,-9,9,0))>.015
            || Math.Abs(Scale-Safe(TargetScale,.96,1.02,1))>.0001 || Math.Abs(Lift-Safe(TargetLift,-.012,.025,0))>.0001
            || Math.Abs(_yawV)+Math.Abs(_rollV)>.04 || Math.Abs(_scaleV)+Math.Abs(_liftV)>.0005;
    }
    private static double Safe(double value,double min,double max,double fallback)=>double.IsFinite(value)?Math.Clamp(value,min,max):fallback;
    private static double Integrate(double value,ref double velocity,double target,double dt)
    {velocity+=((target-value)*150-velocity*17.5)*dt;return value+velocity*dt;}
    public void Reset()
    {Yaw=Roll=Lift=TargetYaw=TargetRoll=TargetLift=_yawV=_rollV=_scaleV=_liftV=0;Scale=TargetScale=1;}
}
