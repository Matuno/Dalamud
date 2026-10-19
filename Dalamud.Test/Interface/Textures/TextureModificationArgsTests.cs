using System.Numerics;

using Dalamud.Interface.Textures;

using TerraFX.Interop.DirectX;

using Xunit;

namespace Dalamud.Test.Interface.Textures;

public class TextureModificationArgsTests
{
    [Fact]
    public void IdentityCopyUsesEffectiveDefaultCoordinates()
    {
        var desc = new D3D11_TEXTURE2D_DESC { Width = 64, Height = 32, Format = DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM };
        TextureModificationArgs defaults = default;
        Assert.True(defaults.IsCompleteSourceCopy(desc));
        Assert.True(new TextureModificationArgs().IsCompleteSourceCopy(desc));
        Assert.True((defaults with { NewWidth = 64, NewHeight = 32, Format = desc.Format }).IsCompleteSourceCopy(desc));
        Assert.False((defaults with { Uv0 = new Vector2(0.25f, 0) }).IsCompleteSourceCopy(desc));
        Assert.False((defaults with { Uv1 = new Vector2(0.75f, 1) }).IsCompleteSourceCopy(desc));
        Assert.False((defaults with { NewWidth = 32 }).IsCompleteSourceCopy(desc));
        Assert.False((defaults with { NewHeight = 16 }).IsCompleteSourceCopy(desc));
        Assert.False((defaults with { MakeOpaque = true }).IsCompleteSourceCopy(desc));
        Assert.False((defaults with { Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM }).IsCompleteSourceCopy(desc));
    }
}
