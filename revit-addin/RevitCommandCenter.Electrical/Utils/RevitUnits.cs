using Autodesk.Revit.DB;

namespace RevitCommandCenter.Electrical.Utils;

/// <summary>
/// Unit conversion between the Revit API (decimal feet, internally, always)
/// and the millimetres/metres the rest of this system speaks.
///
/// Every length crossing the Revit boundary goes through here. Ad-hoc `* 304.8`
/// scattered through handlers is how sign and scale bugs get in.
/// </summary>
public static class RevitUnits
{
    public const double MmPerFoot = 304.8;

    public static double FeetToMm(double feet) => feet * MmPerFoot;
    public static double MmToFeet(double mm) => mm / MmPerFoot;

    public static double FeetToM(double feet) => feet * 0.3048;
    public static double MToFeet(double metres) => metres / 0.3048;

    /// <summary>Square feet to square metres.</summary>
    public static double SqFeetToSqM(double squareFeet) => squareFeet * 0.09290304;

    public static double SqMToSqFeet(double squareMetres) => squareMetres / 0.09290304;

    /// <summary>
    /// Distance between two points, in millimetres.
    /// </summary>
    public static double DistanceMm(XYZ a, XYZ b) => FeetToMm(a.DistanceTo(b));

    /// <summary>
    /// A point <paramref name="distanceMm"/> along <paramref name="direction"/>
    /// from <paramref name="origin"/>.
    ///
    /// <paramref name="direction"/> must be normalized; the offset is converted
    /// to feet before being applied because XYZ arithmetic is in feet.
    /// </summary>
    public static XYZ PointAlong(XYZ origin, XYZ direction, double distanceMm) =>
        origin + direction * MmToFeet(distanceMm);
}
