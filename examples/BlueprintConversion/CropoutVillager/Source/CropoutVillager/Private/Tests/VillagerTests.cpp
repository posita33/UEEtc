#if WITH_DEV_AUTOMATION_TESTS
#include "CropoutVillagerPawn.h"
#include "Components/CapsuleComponent.h"
#include "Components/StaticMeshComponent.h"
#include "Engine/World.h"
#include "GameFramework/PawnMovementComponent.h"
#include "Misc/AutomationTest.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FCropoutVillagerStateTest, "UEEtc.Cropout.VillagerState",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FCropoutVillagerStateTest::RunTest(const FString& Parameters)
{
    const UWorld::InitializationValues Values = UWorld::InitializationValues().AllowAudioPlayback(false).CreatePhysicsScene(false)
        .CreateNavigation(false).CreateAISystem(false).ShouldSimulatePhysics(false);
    // CreateWorld already calls InitializeNewWorld; initializing twice duplicates WorldSettings.
    UWorld* World = UWorld::CreateWorld(EWorldType::Game, false, NAME_None, nullptr, true,
        ERHIFeatureLevel::Num, &Values);
    ACropoutVillagerPawn* Pawn = World->SpawnActor<ACropoutVillagerPawn>();
    if (!TestNotNull(TEXT("Native Pawn can spawn without Cropout assets"), Pawn)) { World->DestroyWorld(false); return false; }
    TestEqual(TEXT("Capsule radius from source"), Pawn->Capsule->GetUnscaledCapsuleRadius(), 30.f);
    TestEqual(TEXT("Movement updates collision root"), Pawn->GetMovementComponent()->UpdatedComponent.Get(), static_cast<USceneComponent*>(Pawn->Capsule));
    Pawn->AddResource(2, 10);
    Pawn->AddResource(3, 4);
    TestEqual(TEXT("Add Resource replaces rather than accumulates"), Pawn->Quantity, 4);
    uint8 Resource = 0; int32 Value = 0;
    Pawn->RemoveResource(Resource, Value);
    TestEqual(TEXT("Removal returns resource type"), Resource, static_cast<uint8>(3));
    TestEqual(TEXT("Removal returns previous quantity"), Value, 4);
    TestEqual(TEXT("Removal clears quantity"), Pawn->Quantity, 0);
    TestEqual(TEXT("Removal resets enum identifier"), Pawn->HeldResource, Pawn->EmptyResource);
    TestFalse(TEXT("Removal hides crate"), Pawn->Tool->IsVisible());
    TestNull(TEXT("Removal clears mesh"), Pawn->Tool->GetStaticMesh());
    // The original BP has unimplemented interface methods. Preserve their output defaults.
    bool Available = true; Value = 9;
    Pawn->CheckResource(2, Available, Value);
    TestFalse(TEXT("Check Resource default"), Available);
    TestEqual(TEXT("Check Resource default count"), Value, 0);
    TestEqual(TEXT("Get Current Resources default, not HeldResource"), Pawn->GetCurrentResources(), Pawn->EmptyResource);
    TestEqual(TEXT("Progress Building is an empty/default implementation"), Pawn->ProgressBuilding(10), 0.f);
    Pawn->AddResource(1, 8);
    Pawn->RemoveTargetResource(1, 3);
    TestEqual(TEXT("Remove Target Resource is a no-op in the source Pawn"), Pawn->Quantity, 8);
    Pawn->StopJob();
    TestEqual(TEXT("Stop Job clears quantity"), Pawn->Quantity, 0);
    TestEqual(TEXT("Stop Job preserves source resource type"), Pawn->HeldResource, static_cast<uint8>(1));
    AActor* Target = World->SpawnActor<AActor>();
    Pawn->ActOnTarget(Target);
    TestEqual(TEXT("Untagged valid target is remembered without starting a job"), Pawn->TargetActor.Get(), Target);
    Pawn->ActOnTarget(nullptr);
    TestEqual(TEXT("Invalid target leaves state unchanged"), Pawn->TargetActor.Get(), Target);
    UDataTable* Jobs = NewObject<UDataTable>(Pawn);
    Jobs->RowStruct = FCropoutJobRow::StaticStruct();
    Jobs->AddRow(TEXT("Idle"), FCropoutJobRow());
    Jobs->AddRow(TEXT("Work"), FCropoutJobRow());
    Pawn->Jobs = Jobs;
    TestTrue(TEXT("Empty-reference job can be selected"), Pawn->ChangeJob(TEXT("Idle")));
    TestTrue(TEXT("Selected job tag installed"), Pawn->Tags.Contains(TEXT("Idle")));
    Pawn->Quantity = 12;
    TestTrue(TEXT("Same job returns without resetting state"), Pawn->ChangeJob(TEXT("Idle")));
    TestEqual(TEXT("Same job preserves held count"), Pawn->Quantity, 12);
    TestTrue(TEXT("Changing job succeeds"), Pawn->ChangeJob(TEXT("Work")));
    TestEqual(TEXT("Changed job resets count"), Pawn->Quantity, 0);
    Pawn->ReturnToDefaultBehavior();
    TestEqual(TEXT("Return to default selects Idle"), Pawn->CurrentJob, FName(TEXT("Idle")));
    UEnum* ResourceEnum = NewObject<UEnum>(Pawn);
    TArray<TPair<FName, int64>> EnumNames = { {FName(TEXT("NewEnumerator0")), 2}, {FName(TEXT("NewEnumerator5")), 7} };
    ResourceEnum->SetEnums(EnumNames, UEnum::ECppForm::Regular, UEnum::EUnderlyingType::uint8,
        EEnumFlags::None, UEnum::EAddMaxKeyIfMissing::Yes);
    TestTrue(TEXT("Resource identifiers resolve from actual enum values"), Pawn->ConfigureResourceEnum(ResourceEnum));
    TestEqual(TEXT("Empty enum value need not equal its name suffix"), Pawn->EmptyResource, static_cast<uint8>(7));
    TestEqual(TEXT("Eat enum value resolves without guessing"), Pawn->EatResource, static_cast<uint8>(2));
    TestFalse(TEXT("Missing enum is rejected"), Pawn->ConfigureResourceEnum(nullptr));
    World->DestroyWorld(false);
    return true;
}
#endif
