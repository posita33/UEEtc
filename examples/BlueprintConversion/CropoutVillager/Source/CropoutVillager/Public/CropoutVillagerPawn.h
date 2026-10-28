#pragma once

#include "CoreMinimal.h"
#include "Engine/DataTable.h"
#include "GameFramework/Pawn.h"
#include "CropoutVillagerPawn.generated.h"

class UAnimMontage;
class UBehaviorTree;
class UCapsuleComponent;
class UDecalComponent;
class UFloatingPawnMovement;
class UPlayMontageCallbackProxy;
class USkeletalMesh;
class USkeletalMeshComponent;
class UStaticMesh;
class UStaticMeshComponent;
struct FStreamableHandle;

// A native replacement schema; the existing Blueprint ST_Job is a different type.
USTRUCT(BlueprintType)
struct CROPOUTVILLAGER_API FCropoutJobRow : public FTableRowBase
{
    GENERATED_BODY()
    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="Job") TSoftObjectPtr<UBehaviorTree> BehaviorTree;
    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="Job") TSoftObjectPtr<UAnimMontage> WorkAnimation;
    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="Job") TSoftObjectPtr<USkeletalMesh> Hat;
    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="Job") TSoftObjectPtr<UStaticMesh> Tool;
};

/** Experimental native Pawn. A child Blueprint bridges the original BPI_* interfaces. */
UCLASS(Blueprintable)
class CROPOUTVILLAGER_API ACropoutVillagerPawn : public APawn
{
    GENERATED_BODY()
public:
    ACropoutVillagerPawn();
    virtual UPawnMovementComponent* GetMovementComponent() const override;
    virtual void OnConstruction(const FTransform& Transform) override;

    UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category="Villager|Components") TObjectPtr<UCapsuleComponent> Capsule;
    UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category="Villager|Components") TObjectPtr<UFloatingPawnMovement> Movement;
    UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category="Villager|Components") TObjectPtr<USkeletalMeshComponent> Body;
    UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category="Villager|Components") TObjectPtr<USkeletalMeshComponent> Hair;
    UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category="Villager|Components") TObjectPtr<USkeletalMeshComponent> Hat;
    UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category="Villager|Components") TObjectPtr<UStaticMeshComponent> Tool;
    UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category="Villager|Components") TObjectPtr<UDecalComponent> Shadow;

    UPROPERTY(EditDefaultsOnly, BlueprintReadOnly, Category="Villager|Jobs", meta=(RequiredAssetDataTags="RowStructure=/Script/CropoutVillager.CropoutJobRow")) TObjectPtr<UDataTable> Jobs;
    UPROPERTY(EditDefaultsOnly, BlueprintReadOnly, Category="Villager|Appearance") TArray<TSoftObjectPtr<USkeletalMesh>> HairOptions;
    UPROPERTY(EditDefaultsOnly, BlueprintReadOnly, Category="Villager|Appearance") TObjectPtr<UStaticMesh> ResourceCrate;
    UPROPERTY(EditDefaultsOnly, BlueprintReadOnly, Category="Villager|Animation") TObjectPtr<UAnimMontage> DeliveryMontage;

    // Bytes intentionally preserve the BP enum's underlying values without inventing display names.
    UPROPERTY(EditDefaultsOnly, BlueprintReadOnly, Category="Villager|Resources") uint8 EmptyResource = 5;
    UPROPERTY(EditDefaultsOnly, BlueprintReadOnly, Category="Villager|Resources") uint8 EatResource = 0;
    UPROPERTY(VisibleInstanceOnly, BlueprintReadOnly, Category="Villager|Resources") uint8 HeldResource = 5;
    UPROPERTY(VisibleInstanceOnly, BlueprintReadOnly, Category="Villager|Resources") int32 Quantity = 0;
    UPROPERTY(VisibleInstanceOnly, BlueprintReadOnly, Category="Villager|Jobs") FName CurrentJob = TEXT("Idle");
    UPROPERTY(VisibleInstanceOnly, BlueprintReadOnly, Category="Villager|Jobs") TObjectPtr<AActor> TargetActor;
    UPROPERTY(VisibleInstanceOnly, BlueprintReadOnly, Category="Villager|Jobs") TObjectPtr<UBehaviorTree> ActiveBehavior;
    UPROPERTY(VisibleInstanceOnly, BlueprintReadOnly, Category="Villager|Animation") TObjectPtr<UAnimMontage> WorkAnimation;
    UPROPERTY(VisibleInstanceOnly, BlueprintReadOnly, Category="Villager|Jobs") TObjectPtr<UStaticMesh> TargetTool;

    UFUNCTION(BlueprintCallable, Category="Villager|Jobs") void ActOnTarget(AActor* Target);
    UFUNCTION(BlueprintCallable, Category="Villager|Jobs") bool ChangeJob(FName Job);
    UFUNCTION(BlueprintCallable, Category="Villager|Jobs") void ReturnToDefaultBehavior();
    UFUNCTION(BlueprintCallable, Category="Villager|Jobs") void ResetJobState();
    UFUNCTION(BlueprintCallable, Category="Villager|Jobs") void StopJob();
    UFUNCTION(BlueprintCallable, Category="Villager|Resources") void AddResource(uint8 Resource, int32 Value);
    UFUNCTION(BlueprintCallable, Category="Villager|Resources") void RemoveResource(uint8& Resource, int32& Value);
    // These three BPI_Resource implementations are empty/default in BP_Villager itself.
    UFUNCTION(BlueprintPure, Category="Villager|Resources") uint8 GetCurrentResources() const { return EmptyResource; }
    UFUNCTION(BlueprintCallable, Category="Villager|Resources") void RemoveTargetResource(uint8 Resource, int32 Value) {}
    UFUNCTION(BlueprintPure, Category="Villager|Resources") void CheckResource(uint8 Resource, bool& Available, int32& Value) const { Available = false; Value = 0; }
    UFUNCTION(BlueprintPure, Category="Villager|Jobs") float ProgressBuilding(float InvestedTime) const { return 0.f; }
    UFUNCTION(BlueprintCallable, Category="Villager|Resources") void Eat();
    UFUNCTION(BlueprintCallable, Category="Villager|Resources") bool ConfigureResourceEnum(UEnum* ResourceEnum);
    UFUNCTION(BlueprintCallable, Category="Villager|Animation") void PlayWorkAnimation(float Duration);
    UFUNCTION(BlueprintCallable, Category="Villager|Animation") float PlayDeliveryAnimation();

    // Implement these in the adapter BP using the ORIGINAL interface message nodes.
    UFUNCTION(BlueprintImplementableEvent, Category="Villager|Bridge") void ConsumeGameResource(uint8 Resource, int32 Value);
    UFUNCTION(BlueprintImplementableEvent, Category="Villager|Bridge") void UpdateAllVillagers();
    UFUNCTION(BlueprintImplementableEvent, Category="Villager|Bridge") void JobLoadFailed(FName Job);

protected:
    virtual void BeginPlay() override;
    virtual void EndPlay(const EEndPlayReason::Type EndPlayReason) override;

private:
    void FinishJobLoad(FCropoutJobRow Definition, uint64 Revision);
    void PlayVillagerAnimation(UAnimMontage* Montage, float Duration);
    void StopAnimationGroup();
    UFUNCTION() void AnimationFinished(FName NotifyName);
    void CancelJobLoad();
    FTimerHandle EatTimer;
    FTimerHandle AnimationTimer;
    uint64 JobRevision = 0;
    TSharedPtr<FStreamableHandle> JobLoad;
    TSharedPtr<FStreamableHandle> HairLoad;
    UPROPERTY(Transient) TObjectPtr<UPlayMontageCallbackProxy> MontageProxy;
};
