using System.Collections;
using System.CommandLine;
using System.Reflection;
using UAssetAPI;
using UAssetAPI.ExportTypes;
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
        throw new Exception($"Hook uses unloaded properties: {unloadedProperties}");

    var functionsToAdd = hook.Exports.Where(i => i is FunctionExport && !i.ObjectName.ToString().StartsWith("orig_") && !i.ObjectName.ToString().StartsWith("ExecuteUbergraph_") && !oClass.FuncMap.ContainsKey(i.ObjectName)).ToList();
    if (functionsToAdd.Count == 0) {
        Console.WriteLine("Nothing to do");
        return;
    }
    var exportMap = functionsToAdd.Select((e, i) => (hook.Exports.IndexOf(e) + 1, i + orig.Exports.Count + 1)).ToDictionary();

    // rename all hooked functions to orig_NAME
    foreach (var hookFn in hook.Exports.Where(i => i is FunctionExport && i.ObjectName.ToString().StartsWith("hook_"))) {
        var baseFn = orig.Exports.FindIndex(e => e is FunctionExport && e.ObjectName.ToString() == hookFn.ObjectName.ToString()[5..]);
        if (baseFn == -1)
            throw new Exception($"Blueprint defines {hookFn.ObjectName} but no function to hook was found");
        orig.Exports[baseFn].ObjectName = orig.AddFName(new FString($"orig_{orig.Exports[baseFn].ObjectName}"));
        oClass.FuncMap[orig.Exports[baseFn].ObjectName] = FPackageIndex.FromExport(baseFn);
    }

    var importsToRewrite = new List<FPackageIndex>();

    // rewrite all imports & exports of functions to the original blueprint
    TraverseAll(functionsToAdd, (i, n, path) => {
        if (i != null && i.IsImport()) {
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
        } else if (i != null && i.IsExport()) {
            if (!exportMap.TryGetValue(i.Index, out var value)) {
                var export = i.ToExport(hook);
                var exportIndex = orig.Exports.FindIndex(e => e.ObjectName == export.ObjectName);
                if (exportIndex == -1)
                    throw new Exception($"Could not find export {export.ObjectName} in blueprint ({path})");
                value = exportIndex + 1;
            }
            i.Index = value;
        } else if (n != null && n.Asset != orig) {
            n.Index = orig.AddNameReference(n.Value);
            n.Asset = orig;
        }
    }, new HashSet<object>([hook], ReferenceEqualityComparer.Instance));

    // import all missing imports recursively
    for (var i = 0; i < importsToRewrite.Count; i++) {
        var import = importsToRewrite[i].ToImport(hook);
        var index = orig.SearchForImport(import.ClassPackage, import.ClassName, import.ObjectName);
        if (index == 0) {
            var newImport = new Import(orig.AddFName(import.ClassPackage.Value),
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
    foreach (var export in functionsToAdd) {
        var fString = export.ObjectName.Value;
        if (fString.Value.StartsWith("hook_"))
            fString = new FString(fString.Value[5..]);
        export.ObjectName = orig.AddFName(fString);
        orig.Exports.Add(export);
        var index = new FPackageIndex(orig.Exports.Count);
        oClass.FuncMap[export.ObjectName] = index;
        oClass.Children = [..oClass.Children, index];
        oClass.CreateBeforeSerializationDependencies.Add(index);
        Console.WriteLine($"Hooked {fString}");
    }

    // set this or crash
    typeof(UAsset).GetField("NamesReferencedFromExportDataCount", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(orig, orig.GetNameMapIndexList().Count);
    orig.Write(output != null ? output.FullName : hookPath.FullName);
}, hookPath, origPath, output, version, mappings);

return await rootCommand.InvokeAsync(args);

void TraverseAll(object o, Action<FPackageIndex?, FName?, string> func, HashSet<object> visitedObjects, string path = "#") {
    if (!o.GetType().IsValueType && !visitedObjects.Add(o)) return;

    if (o is FPackageIndex i) {
        func(i, null, path);
        return;
    }

    if (o is FName n) {
        func(null, n, path);
        return;
    }

    if (o is ClassExport e) {
        foreach (ref var inter in e.Interfaces.AsSpan()) {
            var index = new FPackageIndex(inter.Class);
            func(index, null, path);
            inter.Class = index.Index;
        }
    }

    if (o is IEnumerable t) {
        var ind = 0;
        foreach (var item in t) {
            TraverseAll(item, func, visitedObjects, $"{path}[{ind++}]");
        }
    }

    var type = o.GetType();
    var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
    foreach (var field in fields) {
        var value = field.GetValue(o);
        if (value is null) continue;
        TraverseAll(value, func, visitedObjects, $"{path}.{field.Name}");
    }
}

