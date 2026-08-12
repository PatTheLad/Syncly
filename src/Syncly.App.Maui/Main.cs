using Microsoft.AspNetCore.Components;

namespace Syncly.App.Maui;

public class Main : ComponentBase
{
    protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
    {
        builder.OpenComponent<Syncly.UI.App>(0);
        builder.CloseComponent();
    }
}
