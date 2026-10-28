using AFP.Domain.Entities;
using AFP.Domain.Enums;

namespace AFP.Domain.Rules;

/// <summary>
/// Trained → Registered (Data Engineer / ML CLI) → Approved (Leadership) → Active (Data Engineer) → Retired.
/// Registered/Approved → Rejected (Leadership).
/// </summary>
public static class ModelLifecycle
{
    public static void Register(ModelVersion model, DateTime nowUtc)
    {
        Require(model, ModelStatus.Trained, "register");
        if (string.IsNullOrWhiteSpace(model.ArtifactSha256))
            throw new DomainException("business_rule_violation", "Model has no recorded artifact hash.");
        model.Status = ModelStatus.Registered;
        model.RegisteredAtUtc = nowUtc;
    }

    public static void Approve(ModelVersion model, long approverId, DateTime nowUtc)
    {
        Require(model, ModelStatus.Registered, "approve");
        model.Status = ModelStatus.Approved;
        model.ApprovedByUserId = approverId;
        model.ApprovedAtUtc = nowUtc;
    }

    public static void Reject(ModelVersion model)
    {
        if (model.Status is not (ModelStatus.Registered or ModelStatus.Approved))
            throw new DomainException("invalid_transition", $"Cannot reject a model in status {model.Status}.");
        model.Status = ModelStatus.Rejected;
    }

    public static void Activate(ModelVersion model, ModelVersion? currentlyActive, DateTime nowUtc)
    {
        Require(model, ModelStatus.Approved, "activate");
        if (currentlyActive is not null && currentlyActive.Id != model.Id)
        {
            currentlyActive.Status = ModelStatus.Retired;
            currentlyActive.RetiredAtUtc = nowUtc;
        }
        model.Status = ModelStatus.Active;
        model.ActivatedAtUtc = nowUtc;
    }

    public static void Retire(ModelVersion model, DateTime nowUtc)
    {
        Require(model, ModelStatus.Active, "retire");
        model.Status = ModelStatus.Retired;
        model.RetiredAtUtc = nowUtc;
    }

    private static void Require(ModelVersion model, ModelStatus expected, string action)
    {
        if (model.Status != expected)
            throw new DomainException("invalid_transition",
                $"Cannot {action} a model in status {model.Status}; expected {expected}.");
    }
}
