namespace DesktopBuddy.ModelCore;

/// <summary>Maps pointer attention to a softly interpolated sixteen-direction gaze.</summary>
public readonly record struct CompanionLookDirection(float Horizontal,float Vertical,int Sector);

public static class CompanionLookMotion
{
    public const int DirectionCount=16;
    private const double DeadZone=.08;

    public static CompanionLookDirection Quantize(double horizontal,double vertical)
    {
        if(!double.IsFinite(horizontal)||!double.IsFinite(vertical))return new(0,0,-1);
        var x=Math.Clamp(horizontal,-1,1);var y=Math.Clamp(vertical,-1,1);
        var radius=Math.Min(1,Math.Sqrt(x*x+y*y));
        if(radius<DeadZone)return new(0,0,-1);
        var sector=(int)Math.Round(Math.Atan2(y,x)/(Math.Tau/DirectionCount),MidpointRounding.AwayFromZero);
        sector=(sector%DirectionCount+DirectionCount)%DirectionCount;
        var angle=sector*Math.Tau/DirectionCount;
        var response=Math.Clamp(radius/.50,0,1);
        response=response*response*(3-2*response);
        return new((float)(Math.Cos(angle)*response),(float)(Math.Sin(angle)*response),sector);
    }
}