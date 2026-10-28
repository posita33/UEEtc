#include "CropoutVillagerPawn.h"
#include "AIController.h"
#include "BehaviorTree/BehaviorTree.h"
#include "BehaviorTree/BlackboardComponent.h"
#include "Components/SkeletalMeshComponent.h"
#include "Components/StaticMeshComponent.h"
#include "Engine/AssetManager.h"
#include "Engine/StreamableManager.h"

void ACropoutVillagerPawn::ActOnTarget(AActor* Target)
{
    if (!IsValid(Target)) return;
    TargetActor = Target;
    if (Target->Tags.IsValidIndex(0))
    {
        ChangeJob(Target->Tags[0]);
        UpdateAllVillagers(); // BP adapter sends BPI_GI to the GameInstance.
    }
}

bool ACropoutVillagerPawn::ChangeJob(FName Job)
{
    CurrentJob = Job; // The original sets New Job before looking up the row.
    if (!Jobs || Jobs->GetRowStruct() != FCropoutJobRow::StaticStruct())
    {
        UE_LOG(LogTemp, Warning, TEXT("Villager: Jobs must use the native CropoutJobRow schema"));
        JobLoadFailed(Job);
        return false;
    }
    const FCropoutJobRow* Row = Jobs->FindRow<FCropoutJobRow>(Job, TEXT("Villager ChangeJob"));
    if (!Row) { JobLoadFailed(Job); return false; }
    if (Tags.Contains(Job)) return true; // The graph only reinitializes when the tag is absent.
    Tags = { Job };
    ResetJobState();
    const FCropoutJobRow Definition = *Row;
    const uint64 Revision = JobRevision;
    TArray<FSoftObjectPath> Paths;
    for (const FSoftObjectPath& Path : { Definition.BehaviorTree.ToSoftObjectPath(), Definition.WorkAnimation.ToSoftObjectPath(),
        Definition.Hat.ToSoftObjectPath(), Definition.Tool.ToSoftObjectPath() })
        if (!Path.IsNull()) Paths.AddUnique(Path);
    if (Paths.IsEmpty()) FinishJobLoad(Definition, Revision);
    else
    {
        // One request keeps all dependencies alive. Revision prevents an old job overwriting a newer one.
        JobLoad = UAssetManager::GetStreamableManager().RequestAsyncLoad(Paths,
            FStreamableDelegate::CreateWeakLambda(this, [this, Definition, Revision] { FinishJobLoad(Definition, Revision); }));
    }
    return true;
}

void ACropoutVillagerPawn::FinishJobLoad(FCropoutJobRow Definition, uint64 Revision)
{
    if (Revision != JobRevision) return;
    if ((!Definition.BehaviorTree.IsNull() && !Definition.BehaviorTree.IsValid()) ||
        (!Definition.WorkAnimation.IsNull() && !Definition.WorkAnimation.IsValid()) ||
        (!Definition.Hat.IsNull() && !Definition.Hat.IsValid()) || (!Definition.Tool.IsNull() && !Definition.Tool.IsValid()))
    {
        Tags.Remove(CurrentJob); // Permit retry after a failed request.
        JobLoadFailed(CurrentJob);
        return;
    }
    ActiveBehavior = Definition.BehaviorTree.Get();
    WorkAnimation = Definition.WorkAnimation.Get();
    TargetTool = Definition.Tool.Get();
    Hat->SetSkeletalMeshAsset(Definition.Hat.Get());
    Hat->SetVisibility(true);
    if (AAIController* AI = Cast<AAIController>(GetController()))
    {
        if (ActiveBehavior) AI->RunBehaviorTree(ActiveBehavior);
        if (IsValid(TargetActor))
            if (UBlackboardComponent* Board = AI->GetBlackboardComponent()) Board->SetValueAsObject(TEXT("Target"), TargetActor);
    }
}

void ACropoutVillagerPawn::CancelJobLoad()
{
    ++JobRevision;
    if (JobLoad) { JobLoad->CancelHandle(); JobLoad.Reset(); }
}

void ACropoutVillagerPawn::ReturnToDefaultBehavior() { ChangeJob(TEXT("Idle")); }

void ACropoutVillagerPawn::ResetJobState()
{
    Hat->SetSkeletalMeshAsset(nullptr);
    Tool->SetVisibility(false);
    Tool->SetStaticMesh(nullptr);
    TargetTool = nullptr;
    StopJob();
}

void ACropoutVillagerPawn::StopJob()
{
    CancelJobLoad();
    if (AController* AI = GetController()) AI->StopMovement();
    Tool->SetVisibility(false);
    StopAnimationGroup();
    Quantity = 0;
}
