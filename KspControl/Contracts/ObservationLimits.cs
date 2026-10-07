namespace KspControl.Contracts
{
 /// <summary>Single source for observation argument bounds shared by host validation, bridge clamps and capabilities.</summary>
 public static class ObservationLimits
 {
  public const int MaxPage = 50;
  public const int MaxSnapshotPage = 20;
  public const int MaxOffset = 100000;
  public const int MaxFilter = 128;
  public const int PartControlsPage = 4;
  public const int MaxPartName = 256;
  public const int DefaultPage = 20;
  public const int DefaultPartsPage = 50;
 }
}
