using System.Collections;
using System.CommandLine;
using System.Reflection;
using DeepEqual.Syntax;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.FieldTypes;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

var hookPath = new Argument<FileInfo>(name: "hook", description: "the hook-containing blueprint");
var origPath = new Argument<FileInfo>(name: "original", description: "the original blueprint");
var output = new Option<FileInfo?>(name: "--output", description: "blueprint output, default: overwrite hook");
var version = new Option<EngineVersion>("--ueversion", description: "unreal engine version", getDefaultValue: () => EngineVersion.VER_UE5_4);
var mappings = new Option<FileInfo>(name: "--mappings", description: "unversioned properties") { IsRequired = true };

var rootCommand = new RootCommand("Hook blueprint functions");
rootCommand.AddArgument(hookPath);
rootCommand.AddArgument(origPath);
rootCommand.AddOption(output);
rootCommand.AddOption(version);
rootCommand.AddOption(mappings);
rootCommand.SetHandler((hookPath, origPath, output, version, mappings) => {
    var usmap = new Usmap(mappings.FullName);
    var hook = new UAsset(hookPath.FullName, engineVersion: version, mappings: usmap);
    var orig = new UAsset(origPath.FullName, engineVersion: version, mappings: usmap);

    var oClass = orig.GetClassExport();
    if (oClass is null)
        throw new Exception("provided original is not a blueprint");
    var hClass = hook.GetClassExport();
    if (hClass is null)
        throw new Exception("provided hook is not a blueprint");
    var unloadedProperties = hClass.LoadedProperties.Where(p => oClass.LoadedProperties.All(property => p.Name != property.Name)).ToList();
    if (unloadedProperties.Count > 0)
        throw new Exception($"Hook uses properties not present in original: {unloadedProperties}");

    var functionsToAdd = hook.Exports.Where(i => i is FunctionExport && !i.ObjectName.ToString().StartsWith("orig_") && !i.ObjectName.ToString().StartsWith("ExecuteUbergraph_") && !oClass.FuncMap.ContainsKey(i.ObjectName)).ToList();
    if (functionsToAdd.Count == 0) {
        Console.WriteLine("Nothing to do");
        return;
    }
    var exportMap = functionsToAdd.Select((e, i) => (hook.Exports.IndexOf(e) + 1, i + orig.Exports.Count + 1)).ToDictionary();
    var hookMap = new Dictionary<FunctionExport, FunctionExport>();

    // rename all hooked functions to orig_NAME
    foreach (var hookFn in hook.Exports.Where(i => i is FunctionExport && i.ObjectName.ToString().StartsWith("hook_"))) {
        var baseFnIndex = orig.Exports.FindIndex(e => e is FunctionExport && e.ObjectName.ToString() == hookFn.ObjectName.ToString()[5..]);
        if (baseFnIndex == -1)
            throw new Exception($"Blueprint defines {hookFn.ObjectName} but no function to hook was found");
        var baseFn = orig.Exports[baseFnIndex];
        baseFn.ObjectName = orig.AddFName(new FString($"orig_{baseFn.ObjectName}"));
        oClass.FuncMap[baseFn.ObjectName] = FPackageIndex.FromExport(baseFnIndex);
        hookMap[(FunctionExport)hookFn] = (FunctionExport)baseFn;
    }

    var importsToRewrite = new List<FPackageIndex>();

    var fnameIndexProp = typeof(FName).GetProperty("Index", BindingFlags.NonPublic | BindingFlags.Instance)!;

    // rewrite all imports & exports of functions to the original blueprint
    TraverseAll(functionsToAdd, (i, path) => {
        if (i.IsImport()) {
            var import = i.ToImport(hook);
            var index = orig.SearchForImport(import.ClassPackage, import.ClassName, import.ObjectName);
            if (index == 0) {
                var newImport = new Import(
                    orig.AddFName(import.ClassPackage.Value),
                    orig.AddFName(import.ClassName.Value),
                    import.OuterIndex,
                    orig.AddFName(import.ObjectName.Value),
                    import.bImportOptional);
                if (newImport.OuterIndex.Index != 0)
                    importsToRewrite.Add(newImport.OuterIndex);
                index = orig.AddImport(newImport).Index;
            }
            i.Index = index;
        } else if (i.IsExport()) {
            if (!exportMap.TryGetValue(i.Index, out var value)) {
                var export = i.ToExport(hook);
                var exportIndex = orig.Exports.FindIndex(e => e.ObjectName == export.ObjectName);
                if (exportIndex == -1)
                    throw new Exception($"Could not find export {export.ObjectName} in blueprint ({path})");
                value = exportIndex + 1;
            }
            i.Index = value;
        }
    }, (n, path) => {
        if (n.Asset == orig) return;
        fnameIndexProp.SetValue(n, orig.AddNameReference(n.Value));
        n.Asset = orig;
    }, new HashSet<object>([hook], ReferenceEqualityComparer.Instance));

    // import all missing imports recursively
    for (var i = 0; i < importsToRewrite.Count; i++) {
        var import = importsToRewrite[i].ToImport(hook);
        var index = orig.SearchForImport(import.ClassPackage, import.ClassName, import.ObjectName);
        if (index == 0) {
            var newImport = new Import(
                orig.AddFName(import.ClassPackage.Value),
                orig.AddFName(import.ClassName.Value),
                import.OuterIndex,
                orig.AddFName(import.ObjectName.Value),
                import.bImportOptional);
            if (newImport.OuterIndex.Index != 0)
                importsToRewrite.Add(newImport.OuterIndex);
            index = orig.AddImport(newImport).Index;
        }

        importsToRewrite[i].Index = index;
    }

    // add the hooked functions to the blueprint
    foreach (FunctionExport funcExport in functionsToAdd) {
        var funcName = funcExport.ObjectName.Value;
        var isHook = false;
        if (funcName.Value.StartsWith("hook_")) {
            funcName = new FString(funcName.Value[5..]);
            isHook = true;

            var baseFn = hookMap[funcExport];
            var hookParam = funcExport.LoadedProperties.Where(p =>
                    (p.PropertyFlags & (EPropertyFlags.CPF_Parm | EPropertyFlags.CPF_OutParm | EPropertyFlags.CPF_ReturnParm)) != 0)
                .ToList();
            var baseParam = baseFn.LoadedProperties.Where(p =>
                    (p.PropertyFlags & (EPropertyFlags.CPF_Parm | EPropertyFlags.CPF_OutParm | EPropertyFlags.CPF_ReturnParm)) != 0)
                .ToList();
            if (hookParam.Count != baseParam.Count)
                throw new Exception($"Hooked function {funcExport.ObjectName}'s parameters do not match overriden function");
            for (var i = 0; i < hookParam.Count; i++) {
                var property = hookParam[i];
                var baseProp = baseParam[i];
                // we still check for the correct struct type, but ignore element size
                if (baseProp is FStructProperty bs && property is FStructProperty hs)
                    hs.ElementSize = bs.ElementSize;
                try {
                    property.WithDeepEqual(baseProp).Assert();
                } catch (Exception e) {
                    throw new Exception($"Hooked function {funcExport.ObjectName}'s parameter {property.Name} does not match overriden function", e);
                }
            }
        }
        funcExport.ObjectName = orig.AddFName(funcName);
        orig.Exports.Add(funcExport);
        var index = new FPackageIndex(orig.Exports.Count);
        oClass.FuncMap[funcExport.ObjectName] = index;
        oClass.Children = [..oClass.Children, index];
        oClass.CreateBeforeSerializationDependencies.Add(index);
        Console.WriteLine($"{(isHook ? "Hooked" : "Added function")} {funcName}");
    }

    // set this or crash
    typeof(UAsset).GetField("NamesReferencedFromExportDataCount", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(orig, orig.GetNameMapIndexList().Count);
    var outputFullName = output != null ? output.FullName : hookPath.FullName;
    orig.Write(outputFullName);
    Console.WriteLine($"Hooked blueprint written to {outputFullName}");
}, hookPath, origPath, output, version, mappings);

return await rootCommand.InvokeAsync(args);

void TraverseAll(object o, Action<FPackageIndex, string> packageFunc, Action<FName, string> nameFunc, HashSet<object> visitedObjects, string path = "#") {
    if (!o.GetType().IsValueType && !visitedObjects.Add(o)) return;

    if (o is FPackageIndex i) {
        packageFunc(i, path);
        return;
    }

    if (o is FName n) {
        nameFunc(n, path);
        return;
    }

    if (o is ClassExport e) {
        foreach (ref var inter in e.Interfaces.AsSpan()) {
            var index = new FPackageIndex(inter.Class);
            packageFunc(index, path);
            inter.Class = index.Index;
        }
    }

    if (o is IEnumerable t) {
        var ind = 0;
        foreach (var item in t) {
            TraverseAll(item, packageFunc, nameFunc, visitedObjects, $"{path}[{ind++}]");
        }
    }

    var type = o.GetType();
    var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
    foreach (var field in fields) {
        var value = field.GetValue(o);
        if (value is null) continue;
        TraverseAll(value, packageFunc, nameFunc, visitedObjects, $"{path}.{field.Name}");
    }
}

