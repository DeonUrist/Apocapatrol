$ErrorActionPreference='Stop'
# Compile the actual pure geometry method with minimal managed vector/math substitutes.
# Does not launch the game or load Unity's native runtime.
$taskSource=[IO.File]::ReadAllText((Join-Path $PSScriptRoot '..\TrafficMap.cs'))
$taskStart=$taskSource.IndexOf('internal static float MapExitDistance(')
$taskEnd=$taskSource.IndexOf('private void OnGUI()', $taskStart)
$taskMethod=$taskSource.Substring($taskStart,$taskEnd-$taskStart)
$taskManaged=@'
using System;
public struct Vector2 {
    public float x,y;
    public Vector2(float x,float y){this.x=x;this.y=y;}
    public static Vector2 operator -(Vector2 a,Vector2 b){return new Vector2(a.x-b.x,a.y-b.y);}
}
public static class Mathf {
    public static float Abs(float x){return Math.Abs(x);}
    public static float Max(float x,float y){return Math.Max(x,y);}
}
public static class MapBoundaryChecks {
'@ + $taskMethod + @'
    static void Check(float got,float expected,string label){if(Math.Abs(got-expected)>.02f)throw new Exception(label+": "+got+" != "+expected);Console.WriteLine("PASS: "+label);}
    public static void Run(){
        var q=new[]{new Vector2(-9000,-7500),new Vector2(9000,-7500),new Vector2(9000,7500),new Vector2(-9000,7500)};
        Check(MapExitDistance(new Vector2(0,0),new Vector2(0,1),q),7500,"North edge without 5 km floor or 1 km padding");
        Check(MapExitDistance(new Vector2(0,0),new Vector2(1,0),q),9000,"East edge respects map aspect");
        Check(MapExitDistance(new Vector2(120,0),new Vector2(0,-1),q),7500,"Offset south route");
        float d=(float)(1/Math.Sqrt(2));
        Check(MapExitDistance(new Vector2(0,0),new Vector2(d,d),q),(float)(7500*Math.Sqrt(2)),"Diagonal Coyote route reaches edge rather than projected far corner");
        Check(MapExitDistance(new Vector2(8990,0),new Vector2(1,0),q),10,"Origin near map edge");
        Check(MapExitDistance(new Vector2(9100,0),new Vector2(1,0),q),0,"Already outside and heading away");
        Check(MapExitDistance(new Vector2(9100,0),new Vector2(-1,0),q),18100,"Outside origin crossing entire map");
        var small=new[]{new Vector2(-200,-300),new Vector2(200,-300),new Vector2(200,300),new Vector2(-200,300)};
        Check(MapExitDistance(new Vector2(0,0),new Vector2(1,0),small),200,"Small map has no artificial minimum");
        var rotated=new[]{new Vector2(0,-100),new Vector2(100,0),new Vector2(0,100),new Vector2(-100,0)};
        Check(MapExitDistance(new Vector2(0,0),new Vector2(d,d),rotated),(float)(50*Math.Sqrt(2)),"Rotated map intersection");
        var shifted=new Vector2[4];for(int i=0;i<4;i++)shifted[i]=new Vector2(q[i].x+100000,q[i].y-1000);
        Check(MapExitDistance(new Vector2(100000,-1000),new Vector2(0,1),shifted),7500,"Translation and kilometre origin shift preserve boundary distance");
    }
}
'@
Add-Type -TypeDefinition $taskManaged
[MapBoundaryChecks]::Run()
