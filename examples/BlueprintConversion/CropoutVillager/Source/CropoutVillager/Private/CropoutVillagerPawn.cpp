#include "CropoutVillagerPawn.h"
#include "AIController.h"
#include "Components/CapsuleComponent.h"
#include "Components/DecalComponent.h"
#include "Components/SkeletalMeshComponent.h"
#include "Components/StaticMeshComponent.h"
#include "Engine/AssetManager.h"
#include "Engine/StreamableManager.h"
#include "GameFramework/FloatingPawnMovement.h"
#include "TimerManager.h"

ACropoutVillagerPawn::ACropoutVillagerPawn()
{
    PrimaryActorTick.bCanEverTick = false; // The extracted EventGraph has no Tick logic.
    bReplicates = true;
    SetReplicateMovement(true);
    bUseControllerRotationYaw = true;
    AutoPossessAI = EAutoPossessAI::PlacedInWorld;
    AIControllerClass = AAIController::StaticClass();
    SpawnCollisionHandlingMethod = ESpawnActorCollisionHandlingMethod::AdjustIfPossibleButDontSpawnIfColliding;

    Capsule = CreateDefaultSubobject<UCapsuleComponent>(TEXT("Capsule"));
    SetRootComponent(Capsule);
    Capsule->InitCapsuleSize(30.f, 92.038315f);
    Capsule->SetCollisionProfileName(TEXT("Pawn"));
    Capsule->SetCanEverAffectNavigation(false);
    Movement = CreateDefaultSubobject<UFloatingPawnMovement>(TEXT("Movement"));
    Movement->SetUpdatedComponent(Capsule);
    Movement->MaxSpeed = 300.f;

    Body = CreateDefaultSubobject<USkeletalMeshComponent>(TEXT("Body"));
    Body->SetupAttachment(Capsule);
    Body->SetRelativeLocation(FVector(0, 0, -92.561032));
    Body->SetRelativeRotation(FRotator(0, -90, 0));
    Body->SetCollisionEnabled(ECollisionEnabled::NoCollision);
    Body->SetRenderCustomDepth(true);
    Hair = CreateDefaultSubobject<USkeletalMeshComponent>(TEXT("Hair"));
    Hair->SetupAttachment(Body);
    Hat = CreateDefaultSubobject<USkeletalMeshComponent>(TEXT("Hat"));
    Hat->SetupAttachment(Body, TEXT("headSocket"));
    Tool = CreateDefaultSubobject<UStaticMeshComponent>(TEXT("Tool"));
    Tool->SetupAttachment(Body, TEXT("hand_rSocket"));
    Tool->SetCollisionEnabled(ECollisionEnabled::NoCollision);
    for (USkeletalMeshComponent* Part : { Hair.Get(), Hat.Get() })
    {
        Part->SetCollisionEnabled(ECollisionEnabled::NoCollision);
        Part->SetRenderCustomDepth(true);
    }
    Tool->SetRenderCustomDepth(true);
    Tool->SetCustomDepthStencilValue(1);
    Hat->SetCustomDepthStencilValue(1);
    Shadow = CreateDefaultSubobject<UDecalComponent>(TEXT("Shadow"));
    Shadow->SetupAttachment(Capsule);
    Shadow->SetRelativeLocation(FVector(0, 0, -94.502671));
    Shadow->SetRelativeRotation(FRotator(-90, 360, 0));
    Shadow->SetRelativeScale3D(FVector(.4375, .34375, .34375));
    for (int32 Index = 1; Index <= 6; ++Index)
    {
        HairOptions.Add(TSoftObjectPtr<USkeletalMesh>(FSoftObjectPath(FString::Printf(
            TEXT("/Game/Characters/Meshes/Hair/SKM_Hair%02d.SKM_Hair%02d"), Index, Index))));
    }
}

UPawnMovementComponent* ACropoutVillagerPawn::GetMovementComponent() const { return Movement; }

void ACropoutVillagerPawn::OnConstruction(const FTransform& Transform)
{
    Super::OnConstruction(Transform);
    Body->SetCustomPrimitiveDataFloat(0, FMath::FRand());
    Body->SetCustomPrimitiveDataFloat(1, FMath::FRand());
}

void ACropoutVillagerPawn::BeginPlay()
{
    Super::BeginPlay();
    HeldResource = EmptyResource;
    AddActorWorldOffset(FVector(0, 0, Capsule->GetUnscaledCapsuleHalfHeight()), false);
    GetWorldTimerManager().SetTimer(EatTimer, this, &ThisClass::Eat, 24.f, true);
    ChangeJob(TEXT("Idle"));
    if (!HairOptions.IsEmpty())
    {
        const TSoftObjectPtr<USkeletalMesh> Choice = HairOptions[FMath::RandRange(0, HairOptions.Num() - 1)];
        if (!Choice.IsNull())
        {
            HairLoad = UAssetManager::GetStreamableManager().RequestAsyncLoad(Choice.ToSoftObjectPath(),
                FStreamableDelegate::CreateWeakLambda(this, [this, Choice]
                {
                    if (USkeletalMesh* Mesh = Choice.Get()) Hair->SetSkeletalMeshAsset(Mesh);
                    Hair->SetCustomPrimitiveDataFloat(0, FMath::FRand());
                }));
        }
    }
}

void ACropoutVillagerPawn::EndPlay(const EEndPlayReason::Type Reason)
{
    CancelJobLoad();
    if (HairLoad) { HairLoad->CancelHandle(); HairLoad.Reset(); }
    GetWorldTimerManager().ClearTimer(EatTimer);
    GetWorldTimerManager().ClearTimer(AnimationTimer);
    StopAnimationGroup();
    Super::EndPlay(Reason);
}
