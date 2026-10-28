#include "CropoutVillagerPawn.h"
#include "Components/StaticMeshComponent.h"

bool ACropoutVillagerPawn::ConfigureResourceEnum(UEnum* ResourceEnum)
{
    if (!ResourceEnum) return false;
    const int64 Empty = ResourceEnum->GetValueByNameString(TEXT("NewEnumerator5"));
    const int64 Food = ResourceEnum->GetValueByNameString(TEXT("NewEnumerator0"));
    if (Empty < 0 || Empty > MAX_uint8 || Food < 0 || Food > MAX_uint8) return false;
    EmptyResource = static_cast<uint8>(Empty);
    EatResource = static_cast<uint8>(Food);
    HeldResource = EmptyResource;
    return true;
}

void ACropoutVillagerPawn::AddResource(uint8 Resource, int32 Value)
{
    // Despite its name, the original Add Resource REPLACES the held amount; do not accumulate.
    HeldResource = Resource;
    Quantity = Value;
    Tool->SetVisibility(true);
    Tool->SetStaticMesh(ResourceCrate);
}

void ACropoutVillagerPawn::RemoveResource(uint8& Resource, int32& Value)
{
    Resource = HeldResource;
    Value = Quantity;
    HeldResource = EmptyResource;
    Quantity = 0;
    Tool->SetVisibility(false);
    Tool->SetStaticMesh(nullptr);
}

void ACropoutVillagerPawn::Eat() { ConsumeGameResource(EatResource, 3); }
