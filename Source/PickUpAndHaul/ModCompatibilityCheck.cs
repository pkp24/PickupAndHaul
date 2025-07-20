namespace PickUpAndHaul;
public static class ModCompatibilityCheck
{
	public static bool CombatExtendedIsActive { get; } = ModsConfig.ActiveModsInLoadOrder.Any(m => m.Name.Contains("Combat Extended", StringComparison.InvariantCultureIgnoreCase));

	public static bool AllowToolIsActive { get; } = ModsConfig.ActiveModsInLoadOrder.Any(m => m.Name.Contains("Allow Tool", StringComparison.InvariantCultureIgnoreCase));

	public static bool ExtendedStorageIsActive { get; } = ModsConfig.ActiveModsInLoadOrder.Any(m => m.Name.Contains("ExtendedStorageFluffyHarmonised", StringComparison.InvariantCultureIgnoreCase))
		|| ModsConfig.ActiveModsInLoadOrder.Any(m => m.Name.Contains("Extended Storage", StringComparison.InvariantCultureIgnoreCase))
		|| ModsConfig.ActiveModsInLoadOrder.Any(m => m.Name.Contains("Core SK", StringComparison.InvariantCultureIgnoreCase));

	public static bool HCSKIsActive { get; } = ModsConfig.ActiveModsInLoadOrder.Any(m => m.Name.Contains("Core SK", StringComparison.InvariantCultureIgnoreCase));
}