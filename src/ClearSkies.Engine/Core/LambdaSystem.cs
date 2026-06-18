namespace ClearSkies.Engine.Core;

/// <summary>Wraps an <see cref="Action"/> as an <see cref="ISystem"/> for quick one-liner systems.</summary>
public sealed class LambdaSystem : ISystem
{
    private readonly Action _update;
    public LambdaSystem(Action update) => _update = update;
    public void Update(float dt) => _update();
}
