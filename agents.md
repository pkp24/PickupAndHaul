# PickUpAndHaul Mod Development Guide for ChatGPT Codex

## Project Overview

PickUpAndHaul is a RimWorld mod that allows colonists to gather items in their inventory and haul them to stockpiles, significantly improving hauling efficiency. The mod enables pawns to carry multiple items at once instead of just one item per trip.

## Project Structure

```
PickupAndHaul/
├── 1.6/                          # RimWorld 1.6 version assemblies
│   └── Assemblies/
│       ├── IHoldMultipleThings.dll
│       └── PickUpAndHaul.dll
├── About/                        # Mod metadata
│   └── About.xml
├── Defs/                         # XML definitions
│   └── JobDefs/
│       └── WorkGiver.xml
├── Languages/                    # Localization files
├── Patches/                      # Harmony patches
│   └── PickUpAndHaul.xml
├── Source/                       # C# source code
│   ├── IHoldMultipleThings/     # Dependency mod
│   └── PickUpAndHaul/           # Main mod
└── ReferenceMods/               # Reference mods for development
    ├── Adaptive-Storage-Framework/
    └── NeatStorage/
```

## Core Components

### Main Mod Files
- **Modbase.cs**: Main mod entry point and settings
- **JobDriver_HaulToInventory.cs**: Handles the hauling job logic
- **JobDriver_UnloadYourHauledInventory.cs**: Handles unloading items from inventory
- **WorkGiver_HaulToInventory.cs**: Defines when and how pawns should haul items
- **HarmonyPatches.cs**: Harmony patches for game integration

### Key Features
1. **Inventory Hauling**: Pawns pick up multiple items and carry them in inventory
2. **Smart Pathing**: Optimized pathfinding for hauling routes
3. **Combat Extended Compatibility**: Special handling for CE inventory system
4. **Performance Optimization**: Save/load safety and performance profiling
5. **Multi-language Support**: Extensive localization

## Reference Mods

### Adaptive Storage Framework
**Location**: `ReferenceMods/Adaptive-Storage-Framework/`
**Documentation**: `ReferenceMods/Adaptive-Storage-Framework/Docs/modules/ROOT/pages/index.adoc`
**GitHub**: https://github.com/bbradson/Adaptive-Storage-Framework

This mod provides a framework for creating storage solutions. Key concepts:
- Storage containers with configurable capacity
- Item filtering and sorting
- Integration with RimWorld's storage system
- Modular design for extensibility
- Performance optimization for storage operations
- Visual representation of stored items

**Key Files to Reference**:
- Storage container implementations
- Item filtering logic
- Capacity management systems
- Integration patterns with RimWorld's hauling system
- Performance optimization techniques
- UI components for storage management

**Integration Opportunities**:
- Use ASF's storage capacity calculations for better hauling decisions
- Integrate with ASF's filtering system for item selection
- Leverage ASF's modular architecture for extensible storage solutions
- Reference ASF's performance optimizations for improved mod efficiency

### NeatStorage
**Location**: `ReferenceMods/NeatStorage/`
**GitHub**: https://github.com/pkp24/NeatStorage

This mod focuses on organized storage solutions. Key concepts:
- Storage organization systems
- Item categorization and sorting
- Visual storage management
- User interface for storage control
- Automated storage optimization
- Smart item placement algorithms

**Key Files to Reference**:
- Storage organization logic
- UI components for storage management
- Item categorization systems
- Integration with hauling workflows
- Automated sorting algorithms
- Visual representation systems

**Integration Opportunities**:
- Reference NeatStorage's organization patterns for better item management
- Integrate with categorization systems for smarter hauling priorities
- Use UI components for better user experience
- Implement similar storage workflows for consistency
- Leverage automated sorting for efficient item placement

## Development Guidelines

### Code Style
- Follow C# conventions
- Use meaningful variable names
- Add comments for complex logic
- Implement proper error handling

### RimWorld Integration
- Use Harmony for patches
- Follow RimWorld's job system patterns
- Implement proper save/load compatibility
- Handle mod compatibility gracefully

### Performance Considerations
- Minimize allocations in hot paths
- Use object pooling where appropriate
- Profile performance-critical sections
- Implement proper cleanup in job drivers

### Testing
- Test with different RimWorld versions
- Verify compatibility with popular mods
- Test save/load functionality
- Validate performance impact

## Common Patterns

### Job Driver Pattern
```csharp
public class JobDriver_Example : JobDriver
{
    public override IEnumerable<Toil> MakeNewToils()
    {
        // Define job steps
        yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.ClosestTouch);
        yield return new Toil { /* action */ };
    }
}
```

### Component Pattern
```csharp
public class CompExample : ThingComp
{
    public override void PostSpawnSetup(bool respawningAfterLoad)
    {
        base.PostSpawnSetup(respawningAfterLoad);
        // Initialize component
    }
}
```

### Harmony Patch Pattern
```csharp
[HarmonyPatch(typeof(TargetClass), "TargetMethod")]
public static class PatchClass
{
    public static void Postfix(/* parameters */)
    {
        // Post-patch logic
    }
}
```

## Integration Points

### With Adaptive Storage Framework
- Reference storage capacity calculations for optimal hauling decisions
- Integrate with storage container systems for better item placement
- Use filtering mechanisms for intelligent item selection
- Leverage modular storage architecture for extensible features
- Implement performance optimizations from ASF patterns
- Use ASF's visual representation systems for better UX

### With NeatStorage
- Reference organization patterns for improved item management
- Integrate with categorization systems for smarter hauling priorities
- Use UI components for enhanced user experience
- Implement similar storage workflows for consistency
- Leverage automated sorting algorithms for efficient item placement
- Adopt visual management techniques for better item tracking

## Build Commands

```bash
# Build dependency first
cd Source/IHoldMultipleThings && dotnet build

# Build main mod
cd Source/PickUpAndHaul && dotnet build
```

## Troubleshooting

### Common Issues
1. **Assembly Loading**: Ensure all dependencies are properly referenced
2. **Harmony Patches**: Verify patch targets exist in target RimWorld version
3. **Save Compatibility**: Test save/load with and without mod
4. **Performance**: Profile mod impact on game performance

### Debug Tools
- Use `DebugLog.cs` for logging
- Enable performance profiling
- Check Harmony patch application
- Verify XML definition loading

## Resources

- **RimWorld Modding Wiki**: https://rimworldwiki.com/wiki/Modding_Tutorials
- **Harmony Documentation**: https://github.com/pardeike/Harmony
- **RimWorld API**: Available in RimWorld installation directory
- **Community Forums**: https://ludeon.com/forums/
- **Adaptive Storage Framework**: https://github.com/bbradson/Adaptive-Storage-Framework
- **NeatStorage**: https://github.com/pkp24/NeatStorage

## Development Workflow

1. **Plan Changes**: Understand the impact on existing systems
2. **Reference Mods**: Check how similar features are implemented in ASF and NeatStorage
3. **Implement**: Follow established patterns and conventions
4. **Test**: Verify functionality and performance
5. **Document**: Update this guide with new patterns or insights

## Specific Integration Examples

### Using Adaptive Storage Framework Patterns
```csharp
// Example: Using ASF's capacity calculation
public int CalculateOptimalHaulCount(Thing thing, Pawn pawn)
{
    // Reference ASF's capacity calculation methods
    // Integrate with ASF's filtering system
    // Use ASF's performance optimizations
}
```

### Using NeatStorage Patterns
```csharp
// Example: Using NeatStorage's organization logic
public void OrganizeHauledItems(List<Thing> items)
{
    // Reference NeatStorage's categorization system
    // Use NeatStorage's sorting algorithms
    // Implement similar UI patterns
}
```

## Performance Optimization from Reference Mods

### From Adaptive Storage Framework
- Efficient storage capacity calculations
- Optimized item filtering algorithms
- Reduced memory allocations in storage operations
- Smart caching mechanisms for storage data

### From NeatStorage
- Efficient item categorization systems
- Optimized sorting algorithms
- Smart UI update patterns
- Reduced computational overhead in organization tasks

This comprehensive guide provides ChatGPT Codex with all the necessary information about both reference mods and how to effectively integrate their patterns and features into the PickUpAndHaul mod development process. 