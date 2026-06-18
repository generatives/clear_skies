namespace ClearSkies.Engine.Core;

/// <summary>Ordered stages systems run in each frame.</summary>
public enum SystemStage
{
    Input,
    Logic,
    PreRender,
    Render,
}

/// <summary>Minimal scheduling contract so engine and game systems share one update order.</summary>
public interface ISystem
{
    void Update(float dt);
}
