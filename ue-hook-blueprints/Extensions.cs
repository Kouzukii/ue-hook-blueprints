using UAssetAPI;
using UAssetAPI.UnrealTypes;

public static class Extensions {
    public static FName AddFName(this UAsset uAsset, FString name) {
        return new FName(uAsset, uAsset.AddNameReference(name));
    }
}
