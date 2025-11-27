using Memorizer.Models;

namespace Memorizer.Services;

/// <summary>
/// Holds the current state of embedding dimension validation.
/// Used by UI to display warnings when migration is needed.
/// </summary>
public interface IDimensionMismatchState
{
    /// <summary>
    /// The validation result from the last check.
    /// </summary>
    DimensionValidationResult? LastValidation { get; }

    /// <summary>
    /// Whether there is currently a dimension mismatch requiring migration.
    /// </summary>
    bool HasMismatch { get; }

    /// <summary>
    /// Updates the validation state.
    /// </summary>
    void Update(DimensionValidationResult validation);

    /// <summary>
    /// Clears the mismatch state (e.g., after successful migration).
    /// </summary>
    void Clear();
}

/// <summary>
/// Singleton state holder for dimension mismatch warnings.
/// </summary>
public class DimensionMismatchState : IDimensionMismatchState
{
    private volatile DimensionValidationResult? _lastValidation;

    public DimensionValidationResult? LastValidation => _lastValidation;

    public bool HasMismatch => _lastValidation?.HasMismatch ?? false;

    public void Update(DimensionValidationResult validation)
    {
        _lastValidation = validation;
    }

    public void Clear()
    {
        _lastValidation = null;
    }
}
