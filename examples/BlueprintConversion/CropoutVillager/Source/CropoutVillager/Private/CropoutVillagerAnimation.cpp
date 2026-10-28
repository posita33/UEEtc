#include "CropoutVillagerPawn.h"
#include "Animation/AnimInstance.h"
#include "Components/SkeletalMeshComponent.h"
#include "Components/StaticMeshComponent.h"
#include "PlayMontageCallbackProxy.h"
#include "TimerManager.h"

void ACropoutVillagerPawn::PlayWorkAnimation(float Duration)
{
    PlayVillagerAnimation(WorkAnimation, Duration);
    Tool->SetStaticMesh(TargetTool);
    Tool->SetVisibility(true);
}

float ACropoutVillagerPawn::PlayDeliveryAnimation()
{
    PlayVillagerAnimation(DeliveryMontage, 1.f);
    return 1.f; // This returns immediately, like the original Play Deliver Anim graph.
}

void ACropoutVillagerPawn::PlayVillagerAnimation(UAnimMontage* Montage, float Duration)
{
    GetWorldTimerManager().ClearTimer(AnimationTimer);
    if (MontageProxy)
    {
        MontageProxy->OnCompleted.RemoveDynamic(this, &ThisClass::AnimationFinished);
        MontageProxy->OnInterrupted.RemoveDynamic(this, &ThisClass::AnimationFinished);
    }
    MontageProxy = UPlayMontageCallbackProxy::CreateProxyObjectForPlayMontage(Body, Montage, 1.f);
    if (MontageProxy)
    {
        MontageProxy->OnCompleted.AddDynamic(this, &ThisClass::AnimationFinished);
        MontageProxy->OnInterrupted.AddDynamic(this, &ThisClass::AnimationFinished);
    }
    // Delay(0) resumes on a later frame; SetTimer(0) would disable the timer instead.
    if (Duration > 0.f) GetWorldTimerManager().SetTimer(AnimationTimer, this, &ThisClass::StopAnimationGroup, Duration, false);
    else AnimationTimer = GetWorldTimerManager().SetTimerForNextTick(this, &ThisClass::StopAnimationGroup);
}

void ACropoutVillagerPawn::StopAnimationGroup()
{
    GetWorldTimerManager().ClearTimer(AnimationTimer);
    if (UAnimInstance* Instance = Body->GetAnimInstance()) Instance->Montage_StopGroupByName(0.f, TEXT("DefaultGroup"));
}

void ACropoutVillagerPawn::AnimationFinished(FName NotifyName) { Tool->SetVisibility(false); }
