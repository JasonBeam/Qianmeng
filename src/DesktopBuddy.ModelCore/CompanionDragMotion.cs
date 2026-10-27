namespace DesktopBuddy.ModelCore;

/// <summary>Low-pass drag velocity with separate run entry and exit thresholds.</summary>
public sealed class CompanionDragMotion
{
    public const double RunEnterPixelsPerSecond=72;
    public const double RunExitPixelsPerSecond=26;
    private const double ResponseSeconds=.055;
    private const double StopDecaySeconds=.12;

    public double Velocity { get; private set; }
    public int RunDirection { get; private set; }

    public double Sample(double displacement,double seconds)
    {
        var dt=double.IsFinite(seconds)?Math.Clamp(seconds,.012,.20):.016;
        var distance=double.IsFinite(displacement)?Math.Clamp(displacement,-360,360):0;
        var raw=Math.Clamp(distance/dt,-1800,1800);
        var blend=1-Math.Exp(-dt/ResponseSeconds);
        Velocity+=(raw-Velocity)*blend;
        UpdateDirection();
        return Velocity;
    }

    public double Decay(double seconds)
    {
        var dt=double.IsFinite(seconds)?Math.Clamp(seconds,0,.12):0;
        Velocity*=Math.Exp(-dt/StopDecaySeconds);
        UpdateDirection();
        return Velocity;
    }

    public void Reset(){Velocity=0;RunDirection=0;}

    private void UpdateDirection()
    {
        var speed=Math.Abs(Velocity);
        if(RunDirection==0&&speed>=RunEnterPixelsPerSecond)RunDirection=Math.Sign(Velocity);
        else if(RunDirection!=0&&speed<=RunExitPixelsPerSecond)RunDirection=0;
        else if(RunDirection!=0&&Math.Sign(Velocity)!=RunDirection&&speed>=RunEnterPixelsPerSecond)RunDirection=Math.Sign(Velocity);
    }
}