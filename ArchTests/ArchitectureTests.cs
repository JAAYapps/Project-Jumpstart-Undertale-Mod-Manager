using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

// If this alias line won't compile, your root namespace differs — fix just this line.
using AppMarker = Project_Jumpstart_Undertale_Mod_Manager.App;

namespace ArchTests;

public class ArchitectureTests
{
    private static readonly Assembly App = typeof(AppMarker).Assembly;

    private const string RootNs       = "Project_Jumpstart_Undertale_Mod_Manager";
    private const string ServicesNs   = RootNs + ".Services";
    private const string ViewModelsNs = RootNs + ".ViewModels";

    // Rule 4: nothing sits in the flat Services namespace — must be Services/<Feature>/.
    [Fact]
    public void Services_use_a_subfolder_not_flat_Services()
    {
        var flat = Types()
            .Where(t => t.Namespace == ServicesNs)   // exactly "...Services", not "...Services.Launcher"
            .Select(t => t.FullName)
            .ToList();

        Assert.True(flat.Count == 0,
            "In flat Services namespace, move to Services/<Feature>/: " + string.Join(", ", flat));
    }

    // Rule 3: no static service classes (a static class can't be injected).
    [Fact]
    public void No_static_service_classes()
    {
        var statics = ServiceTypes()
            .Where(t => t.IsClass && t.IsAbstract && t.IsSealed)   // C# 'static class' in IL
            .Select(t => t.FullName)
            .ToList();

        Assert.True(statics.Count == 0,
            "Static classes not allowed under Services (use an injectable instance class): "
            + string.Join(", ", statics));
    }

    // Rule 2: every concrete XxxService implements IXxxService.
    // (Want "implements ANY interface" instead? swap the check for svc.GetInterfaces().Any().)
    [Fact]
    public void Every_Service_implements_its_matching_interface()
    {
        var offenders = ServiceTypes()
            .Where(t => t.IsClass && !t.IsAbstract && t.Name.EndsWith("Service"))
            .Where(svc => svc.GetInterfaces().All(i => i.Name != "I" + svc.Name))
            .Select(svc => $"{svc.FullName} should implement I{svc.Name}")
            .ToList();

        Assert.True(offenders.Count == 0, string.Join("; ", offenders));
    }

    // Rule 1: ViewModels take interfaces, never concrete service classes.
    // Catches ctor params, fields, properties. (A raw `new LauncherService()` inside a
    // method body is invisible to reflection — that's the one gap a Roslyn analyzer would close.)
    [Fact]
    public void ViewModels_depend_on_service_interfaces_not_concrete_classes()
    {
        var concrete = ServiceTypes()
            .Where(t => t.IsClass && !t.IsAbstract && t.Name.EndsWith("Service"))
            .ToHashSet();

        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var offenders = new List<string>();

        foreach (var vm in Types().Where(t => t.IsClass && InNs(t, ViewModelsNs)))
        {
            foreach (var p in vm.GetConstructors().SelectMany(c => c.GetParameters()))
                if (concrete.Contains(p.ParameterType))
                    offenders.Add($"{vm.Name} ctor takes concrete {p.ParameterType.Name} (use I{p.ParameterType.Name})");

            foreach (var f in vm.GetFields(flags))
                if (concrete.Contains(f.FieldType))
                    offenders.Add($"{vm.Name} field is concrete {f.FieldType.Name} (use I{f.FieldType.Name})");

            foreach (var pr in vm.GetProperties(flags))
                if (concrete.Contains(pr.PropertyType))
                    offenders.Add($"{vm.Name} property is concrete {pr.PropertyType.Name} (use I{pr.PropertyType.Name})");
        }

        Assert.True(offenders.Count == 0, string.Join("; ", offenders));
    }

    // --- helpers ---

    private static bool InNs(Type t, string ns) =>
        t.Namespace != null && (t.Namespace == ns || t.Namespace.StartsWith(ns + "."));

    private static IEnumerable<Type> ServiceTypes() =>
        Types().Where(t => InNs(t, ServicesNs));

    private static IEnumerable<Type> Types() =>
        SafeGetTypes(App).Where(t => t.Namespace != null && !t.Name.Contains('<')); // skip compiler-generated

    private static Type[] SafeGetTypes(Assembly asm)
    {
        try { return asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null).ToArray()!; }
    }
}