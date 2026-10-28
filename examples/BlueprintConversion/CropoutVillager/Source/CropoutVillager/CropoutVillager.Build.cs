using UnrealBuildTool;

public class CropoutVillager : ModuleRules
{
    public CropoutVillager(ReadOnlyTargetRules Target) : base(Target)
    {
        PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;
        PublicDependencyModuleNames.AddRange(new[] { "Core", "CoreUObject", "Engine", "AIModule" });
        PrivateDependencyModuleNames.AddRange(new[] { "AnimGraphRuntime" });
    }
}
