using Content.StationArchitect;
using NUnit.Framework;

namespace StationArchitect.Tests;

[TestFixture]
[Parallelizable(ParallelScope.Children)]
public sealed class MapFeatureRequirementTests
{
    private static PressureState State() => new()
    {
        Rooms = [new() { Path = "Alpha/Work", InstanceId = "1", Area = 40, Width = 8, Height = 5, Environment = new() }],
        Departments = [new() { Path = "Alpha", Width = 8, Height = 5 }]
    };

    private static RoomContentRequirement Machine(int min = 1, int target = 1, int max = 2, int area = 12) => new()
    {
        Id = "Fabricator", Prototypes = ["Autolathe", "Protolathe"],
        Count = new() { Min = min, Target = target, Max = max }, MinArea = area
    };

    [Test]
    public void SourceEquipmentEnvironmentAndChoicesSurviveYamlAndTreeCompilation()
    {
        var source = ProgramTestData.Minimal();
        source.SourceRefs = ["Resources/Maps/box.yml"];
        var owner = source.Departments[0]; owner.SourceRefs = ["Resources/Maps/packed.yml#uid:1"];
        var room = owner.Rooms[0]; room.SourceRefs = ["Resources/Maps/box.yml#uid:42"];
        room.Contents = [Machine(area: 30)];
        room.Access.EntryOwners = ["Alpha"];
        source.Choices = [new() { Id = "PowerFamily", Description = "A configurable functional choice.", Rooms = ["Alpha/Work"] }];
        var imported = ProgramYaml.Import(ProgramYaml.Export(source));
        Assert.That(imported.Success, Is.True, ProgramTestData.Errors(imported.Diagnostics));
        var compiled = RequirementCompiler.Compile(imported.Document!);
        var node = compiled.Tree!.Children[0].Children[0];
        Assert.Multiple(() =>
        {
            Assert.That(compiled.Success, Is.True);
            Assert.That(node.Contents[0].Prototypes, Is.EquivalentTo(new[] { "Autolathe", "Protolathe" }));
            Assert.That(node.SourceRefs, Is.EqualTo(room.SourceRefs));
            Assert.That(compiled.Tree.SourceRefs, Is.EqualTo(source.SourceRefs));
            Assert.That(compiled.Tree.Children[0].SourceRefs, Is.EqualTo(owner.SourceRefs));
            Assert.That(node.Environment!.Pressure, Is.EqualTo("standard"));
            Assert.That(node.Access!.EntryOwners, Is.EqualTo(new[] { "Alpha" }));
            Assert.That(node.EffectiveMinArea, Is.EqualTo(30));
            Assert.That(node.ReservedContentArea, Is.EqualTo(30));
            Assert.That(node.ChoiceIds, Is.EqualTo(new[] { "PowerFamily" }));
            Assert.That(compiled.Choices[0].Description, Does.Contain("functional"));
        });
    }

    [Test]
    public void EquipmentReservationsCannotExceedRoomOrParentCapacity()
    {
        var source = ProgramTestData.Minimal();
        source.Departments[0].Rooms[0].Contents = [Machine(min: 2, target: 2, area: 61)];
        Assert.That(RequirementCompiler.Compile(source).Diagnostics.Any(d => d.Code == "equipment-area"), Is.True);
        source.Departments[0].Rooms[0].Contents = [Machine(area: 60)];
        source.Departments[0].Area.Max = 50;
        Assert.That(RequirementCompiler.Compile(source).Diagnostics.Any(d => d.Code == "impossible-area"), Is.True);
    }

    [TestCase("Invalid Prototype")]
    [TestCase("../machine")]
    [TestCase("")]
    public void EquipmentPrototypeTokensAreValidated(string prototype)
    {
        var source = ProgramTestData.Minimal();
        var content = Machine(); content.Prototypes = [prototype]; source.Departments[0].Rooms[0].Contents.Add(content);
        Assert.That(RequirementCompiler.Compile(source).Success, Is.False);
    }

    [Test]
    public void EquipmentRequirementsNeedUniqueIdsAndNonnegativeReservations()
    {
        var source = ProgramTestData.Minimal(); source.Departments[0].Rooms[0].Contents = [Machine(), Machine()];
        Assert.That(RequirementCompiler.Compile(source).Diagnostics.Any(d => d.Code == "duplicate"), Is.True);
        source.Departments[0].Rooms[0].Contents = [Machine(area: -1)];
        Assert.That(RequirementCompiler.Compile(source).Success, Is.False);
    }

    [Test]
    public void MissingEquipmentObservationsArePendingInGrowthAndErrorsAtFinal()
    {
        var source = ProgramTestData.Minimal(); source.Departments[0].Rooms[0].Contents = [Machine()];
        var growth = PressureEvaluator.Evaluate(source, State());
        var final = PressureEvaluator.Evaluate(source, State(), final: true);
        Assert.Multiple(() =>
        {
            Assert.That(growth.Success, Is.True); Assert.That(growth.Complete, Is.False);
            Assert.That(final.Success, Is.False);
            Assert.That(final.Diagnostics.Any(d => d.Code == "content-measurement"), Is.True);
        });
    }

    [Test]
    public void AllowedEquipmentAlternativesAggregateAndRespectActualReservedFloor()
    {
        var source = ProgramTestData.Minimal(); source.Departments[0].Rooms[0].Contents = [Machine(2, 2, 3, 12)];
        var state = State(); state.Rooms[0].Contents =
        [new() { Id = "Fabricator", Prototype = "Autolathe", Count = 1 }, new() { Id = "Fabricator", Prototype = "Protolathe", Count = 1 }];
        Assert.That(PressureEvaluator.Evaluate(source, state, final: true).Success, Is.True);
        state.Rooms[0].Contents![0].Count = 3;
        Assert.That(PressureEvaluator.Evaluate(source, state).Diagnostics.Any(d => d.Code == "count-bound"), Is.True);
        state.Rooms[0].Contents = [new() { Id = "Fabricator", Prototype = "Autolathe", Count = 3 }];
        state.Rooms[0].Area = 30;
        Assert.That(PressureEvaluator.Evaluate(source, state).Diagnostics.Any(d => d.Code == "content-area"), Is.True);
    }

    [Test]
    public void UnknownOrDuplicateEquipmentObservationsCannotSatisfyRequirements()
    {
        var source = ProgramTestData.Minimal(); source.Departments[0].Rooms[0].Contents = [Machine()];
        var state = State(); state.Rooms[0].Contents = [new() { Id = "Fabricator", Prototype = "MadeUpMachine", Count = 1 }];
        Assert.That(PressureEvaluator.Evaluate(source, state).Diagnostics.Any(d => d.Code == "content-reference"), Is.True);
        state.Rooms[0].Contents = [new() { Id = "Fabricator", Prototype = "Autolathe", Count = 1 }, new() { Id = "Fabricator", Prototype = "Autolathe", Count = 1 }];
        Assert.That(PressureEvaluator.Evaluate(source, state).Diagnostics.Any(d => d.Code == "content-measurement"), Is.True);
    }

    [Test]
    public void EnvironmentAndRestrictedEntryObservationsCannotBeSilentlyOmitted()
    {
        var source = ProgramTestData.Minimal(); source.Departments[0].Rooms[0].Access.EntryOwners = ["Alpha"];
        var state = State(); state.Rooms[0].Environment = null;
        var growth = PressureEvaluator.Evaluate(source, state);
        var final = PressureEvaluator.Evaluate(source, state, final: true);
        Assert.That(growth.Success, Is.True); Assert.That(growth.Complete, Is.False);
        Assert.That(final.Diagnostics.Select(d => d.Code), Does.Contain("environment-measurement").And.Contain("entry-owner-measurement"));
        state.Rooms[0].Environment = new(); state.Rooms[0].EntryOwners = ["Alpha"];
        Assert.That(PressureEvaluator.Evaluate(source, state, final: true).Success, Is.True);
    }

    [Test]
    public void ExteriorVacuumRoomsRequireMatchingEnvironmentAndSealing()
    {
        var source = ProgramTestData.Minimal(); source.Departments[0].Rooms[0].Environment = new() { Pressure = "vacuum", Placement = "exterior", Sealed = false };
        var state = State();
        Assert.That(PressureEvaluator.Evaluate(source, state, final: true).Diagnostics.Any(d => d.Code == "environment-bound"), Is.True);
        state.Rooms[0].Environment = new() { Pressure = "vacuum", Placement = "exterior", Sealed = false };
        Assert.That(PressureEvaluator.Evaluate(source, state, final: true).Success, Is.True);
        source.Departments[0].Rooms[0].Environment.Sealed = true;
        Assert.That(PressureEvaluator.Evaluate(source, state, final: true).Success, Is.False);
    }

    [Test]
    public void DirectEntryOwnersAreHardRestrictionsAndUnknownOwnersAreInvalid()
    {
        var source = ProgramTestData.Minimal(); source.Departments[0].Rooms[0].Access.EntryOwners = ["_maintenance"];
        var state = State(); state.Rooms[0].EntryOwners = ["Alpha"];
        Assert.That(PressureEvaluator.Evaluate(source, state).Diagnostics.Any(d => d.Code == "entry-owner"), Is.True);
        state.Rooms[0].EntryOwners = ["_maintenance"];
        Assert.That(PressureEvaluator.Evaluate(source, state, final: true).Success, Is.True);
        source.Departments[0].Rooms[0].Access.EntryOwners = ["Unknown"];
        Assert.That(RequirementCompiler.Compile(source).Success, Is.False);
    }

    [Test]
    public void ChoiceCardinalityCountsTemplatesRatherThanRoomInstances()
    {
        var source = ProgramTestData.Minimal();
        source.Departments[0].Rooms[0].Count = new() { Min = 1, Target = 2, Max = 3 };
        source.Choices = [new() { Id = "Family", Rooms = ["Alpha/Work"], Count = new() { Min = 1, Target = 1, Max = 1 } }];
        var state = State(); var second = ProgramJson.Clone(state.Rooms[0]); second.InstanceId = "2"; state.Rooms.Add(second);
        state.Departments[0].Width = 8; state.Departments[0].Height = 10;
        Assert.That(PressureEvaluator.Evaluate(source, state, final: true).Success, Is.True);
    }

    [Test]
    public void ChoiceMinimumsAreDeferredInGrowthAndMaximumsRemainHard()
    {
        var source = ProgramTestData.Minimal(); var owner = source.Departments[0];
        owner.Rooms[0].Count.Min = 0; owner.Rooms.Add(ProgramTestData.Optional("Alternative"));
        source.Choices = [new() { Id = "Power", Rooms = ["Alpha/Work", "Alpha/Alternative"], Count = new() { Min = 1, Target = 1, Max = 1 } }];
        Assert.That(PressureEvaluator.Evaluate(source, new()).Success, Is.True);
        Assert.That(PressureEvaluator.Evaluate(source, new(), final: true).Diagnostics.Any(d => d.Path == "choices:Power" && d.Code == "activeRooms-bound"), Is.True);
        var state = State(); state.Rooms.Add(new() { Path = "Alpha/Alternative", Area = 9, Width = 3, Height = 3, Environment = new() });
        Assert.That(PressureEvaluator.Evaluate(source, state).Diagnostics.Any(d => d.Path == "choices:Power" && d.Code == "activeRooms-bound"), Is.True);
    }

    [Test]
    public void ChoiceReferencesBoundsAndMandatoryMembersAreValidated()
    {
        var source = ProgramTestData.Minimal(); source.Choices = [new() { Id = "Power", Rooms = ["Alpha/Work"], Count = new() { Min = 0, Target = 0, Max = 0 } }];
        Assert.That(RequirementCompiler.Compile(source).Diagnostics.Any(d => d.Code == "impossible-choice"), Is.True);
        source.Choices[0].Rooms = ["Alpha/Unknown"];
        Assert.That(RequirementCompiler.Compile(source).Diagnostics.Any(d => d.Code == "reference"), Is.True);
        source.Choices[0].Rooms = ["Alpha/Work"]; source.Choices[0].Count.Max = 2;
        Assert.That(RequirementCompiler.Compile(source).Success, Is.False);
    }

    [Test]
    public void UniqueChoiceAlternativeBecomesRequiredWithoutMutatingAuthoredCounts()
    {
        var source = ProgramTestData.Minimal(); source.Departments[0].Rooms[0].Count.Min = 0;
        source.Choices = [new() { Id = "OnlyChoice", Rooms = ["Alpha/Work"] }];
        var compiled = RequirementCompiler.Compile(source);
        Assert.That(compiled.Success, Is.True);
        Assert.That(compiled.Tree!.Children[0].Children[0].EffectiveMinCount, Is.EqualTo(1));
        Assert.That(source.Departments[0].Rooms[0].Count.Min, Is.Zero);
    }

    [Test]
    public void IndividuallyFeasibleChoicesCannotCollectivelyExceedTheirOwnerBudget()
    {
        var source = ProgramTestData.Minimal(); var owner = source.Departments[0];
        owner.Area = ProgramTestData.Area(0, 50, 70);
        owner.Rooms = [ProgramTestData.Room("PowerA", 40, 50, 60), ProgramTestData.Room("PowerB", 40, 50, 60), ProgramTestData.Room("PowerC", 40, 50, 60)];
        foreach (var room in owner.Rooms) room.Count = new() { Min = 0, Target = 0, Max = 1 };
        source.Choices = [new() { Id = "TwoPowerRooms", Rooms = ["Alpha/PowerA", "Alpha/PowerB", "Alpha/PowerC"], Count = new() { Min = 2, Target = 2, Max = 2 } }];
        Assert.That(RequirementCompiler.Compile(source).Diagnostics.Any(d => d.Code == "impossible-choice"), Is.True);
    }

    private static StationProgramDocument WithInterface(string kind, params string[] interfaces)
    {
        var source = ProgramTestData.Minimal(); source.Departments[0].Rooms.Add(ProgramTestData.Room("Public", 9, 16, 25));
        source.Connections = [new() { Id = "Hatch", From = "Alpha/Work", To = ["Alpha/Public"], Modes = ["direct"], Kind = kind, InterfaceTypes = interfaces.ToList(), MinWidth = 2 }];
        return source;
    }

    private static PressureState InterfaceState(string kind = "interaction", string type = "counter", int width = 2)
    {
        var state = State(); state.Rooms.Add(new() { Path = "Alpha/Public", Area = 16, Width = 4, Height = 4, Environment = new() }); state.Departments[0].Width = 12;
        state.Connections = [new() { Id = "$station:Hatch", SourceInstance = "Alpha/Work#1", TargetInstance = "Alpha/Public#1", Mode = "direct", Kind = kind, InterfaceType = type, Width = width }];
        return state;
    }

    [Test]
    public void ServiceCountersDoNotProvideWalkingConnections()
    {
        var source = WithInterface("interaction", "counter", "window");
        Assert.That(PressureEvaluator.Evaluate(source, InterfaceState(), final: true).Success, Is.True);
        source.Connections[0].Kind = "walking";
        Assert.That(RequirementCompiler.Compile(source).Success, Is.False);
        source.Connections[0].InterfaceTypes = ["doorway", "counter"];
        Assert.That(PressureEvaluator.Evaluate(source, InterfaceState(), final: true).Diagnostics.Any(d => d.Code == "measurement-kind"), Is.True);
    }

    [Test]
    public void ConfiguredInterfaceMeasurementsAndWidthAreRequired()
    {
        var source = WithInterface("interaction", "counter");
        var missing = InterfaceState(type: "", width: 0);
        Assert.That(PressureEvaluator.Evaluate(source, missing).Complete, Is.False);
        Assert.That(PressureEvaluator.Evaluate(source, missing, final: true).Success, Is.False);
        Assert.That(PressureEvaluator.Evaluate(source, InterfaceState(width: 1)).Diagnostics.Any(d => d.Code == "interface-bound"), Is.True);
        Assert.That(PressureEvaluator.Evaluate(source, InterfaceState(), final: true).Success, Is.True);
    }

    [Test]
    public void InteractionLinksCannotUseAnIndirectNetwork()
    {
        var source = WithInterface("interaction", "counter"); source.Connections[0].Modes = ["publicHall"];
        Assert.That(RequirementCompiler.Compile(source).Success, Is.False);
    }

    [TestCase("all", false)]
    [TestCase("any", true)]
    public void DirectWalkingRulesRespectEntryOwnersWithAlternativeTargets(string selection, bool expected)
    {
        var source = ProgramTestData.Minimal();
        source.Departments.Add(new() { Id = "Beta", Rooms = [ProgramTestData.Room("Private")] });
        source.Departments[1].Rooms[0].Access.EntryOwners = ["Beta"];
        source.Departments[0].Rooms.Add(ProgramTestData.Room("Internal"));
        source.Connections = [new() { Id = "PrivateRoute", From = "Alpha/Work", To = ["Beta/Private", "Alpha/Internal"], Selection = selection, Modes = ["direct"] }];
        Assert.That(RequirementCompiler.Compile(source).Success, Is.EqualTo(expected));
    }

    [Test]
    public void OptionalPrivateRoomCanStayAbsentButCannotSupplyForbiddenDirectWalkingEvidence()
    {
        var source = ProgramTestData.Minimal();
        source.Departments.Add(new() { Id = "Beta", Area = ProgramTestData.Area(0, 16, 100), Rooms = [ProgramTestData.Optional("Private")] });
        source.Departments[1].Rooms[0].Access.EntryOwners = ["Beta"];
        source.Connections = [new() { Id = "PrivateRoute", From = "Alpha/Work", To = ["Beta/Private"], Modes = ["direct"] }];
        Assert.That(RequirementCompiler.Compile(source).Success, Is.True);
        var state = State(); state.Rooms.Add(new() { Path = "Beta/Private", Area = 16, Width = 4, Height = 4, Environment = new(), EntryOwners = [] });
        state.Departments.Add(new() { Path = "Beta", Width = 4, Height = 4 });
        state.Connections.Add(new() { Id = "$station:PrivateRoute", SourceInstance = "Alpha/Work#1", TargetInstance = "Beta/Private#1", Mode = "direct" });
        Assert.That(PressureEvaluator.Evaluate(source, state).Diagnostics.Any(d => d.Code == "measurement-access"), Is.True);
        source.Connections[0].Modes = ["maintenance"];
        state.Connections[0].Mode = "maintenance";
        Assert.That(PressureEvaluator.Evaluate(source, state, final: true).Success, Is.True,
            "A network route can reach the private room through an eligible gateway without a direct foreign-owner door.");
    }

    [Test]
    public void ServiceInteractionDoesNotCreateAnExcludedWalkingEntry()
    {
        var source = ProgramTestData.Minimal();
        source.Departments.Add(new() { Id = "Beta", Rooms = [ProgramTestData.Room("Private")] });
        source.Departments[1].Rooms[0].Access.EntryOwners = ["Beta"];
        source.Connections = [new() { Id = "ServingCounter", From = "Alpha/Work", To = ["Beta/Private"], Kind = "interaction", Modes = ["direct"], InterfaceTypes = ["counter"] }];
        Assert.That(RequirementCompiler.Compile(source).Success, Is.True);
    }

    [Test]
    public void EquipmentPlacementAndTemperatureRangesSurviveYamlAndCompiledTree()
    {
        var source = ProgramTestData.Minimal(); var room = source.Departments[0].Rooms[0];
        room.Contents = [Machine()]; room.Contents[0].Placement = "spaceBoundary";
        room.Environment.MinTemperatureKelvin = 250; room.Environment.MaxTemperatureKelvin = 273.15;
        var imported = ProgramYaml.Import(ProgramYaml.Export(source));
        Assert.That(imported.Success, Is.True, ProgramTestData.Errors(imported.Diagnostics));
        var node = RequirementCompiler.Compile(imported.Document!).Tree!.Children[0].Children[0];
        Assert.That(node.Contents[0].Placement, Is.EqualTo("spaceBoundary"));
        Assert.That(node.Environment!.MinTemperatureKelvin, Is.EqualTo(250));
        Assert.That(node.Environment.MaxTemperatureKelvin, Is.EqualTo(273.15));
    }

    [TestCase(-1, 300)]
    [TestCase(0, 100001)]
    [TestCase(300, 250)]
    [TestCase(double.NaN, 300)]
    [TestCase(0, double.PositiveInfinity)]
    public void InvalidTemperatureRangesAreRejected(double min, double max)
    {
        var source = ProgramTestData.Minimal(); var environment = source.Departments[0].Rooms[0].Environment;
        environment.MinTemperatureKelvin = min; environment.MaxTemperatureKelvin = max;
        Assert.That(RequirementCompiler.Compile(source).Diagnostics.Any(d => d.Code == "temperature-range"), Is.True);
    }

    [Test]
    public void TemperatureRequirementsNeedObservedValuesAndEnforceTheirBounds()
    {
        var source = ProgramTestData.Minimal(); source.Departments[0].Rooms[0].Environment.MaxTemperatureKelvin = 273.15;
        var state = State();
        Assert.That(PressureEvaluator.Evaluate(source, state).Complete, Is.False);
        Assert.That(PressureEvaluator.Evaluate(source, state, final: true).Diagnostics.Any(d => d.Code == "temperature-measurement"), Is.True);
        state.Rooms[0].Environment!.TemperatureKelvin = 293.15;
        var growth = PressureEvaluator.Evaluate(source, state);
        Assert.That(growth.Success, Is.True); Assert.That(growth.Complete, Is.False);
        Assert.That(PressureEvaluator.Evaluate(source, state, final: true).Diagnostics.Any(d => d.Code == "temperature-bound"), Is.True);
        state.Rooms[0].Environment!.TemperatureKelvin = 270;
        Assert.That(PressureEvaluator.Evaluate(source, state, final: true).Success, Is.True);
        state.Rooms[0].Environment!.TemperatureKelvin = double.NaN;
        Assert.That(PressureEvaluator.Evaluate(source, state).Diagnostics.Any(d => d.Code == "temperature-measurement"), Is.True);
    }

    [TestCase("floor", "floor", true)]
    [TestCase("floor", "boundary", false)]
    [TestCase("boundary", "spaceBoundary", true)]
    [TestCase("spaceBoundary", "boundary", false)]
    [TestCase("spaceBoundary", "floor", false)]
    [TestCase("spaceBoundary", "spaceBoundary", true)]
    public void EquipmentBoundaryRequirementsUseActualObservedPlacement(string required, string observed, bool succeeds)
    {
        var source = ProgramTestData.Minimal(); source.Departments[0].Rooms[0].Contents = [Machine()];
        source.Departments[0].Rooms[0].Contents[0].Placement = required;
        var state = State(); state.Rooms[0].Contents = [new() { Id = "Fabricator", Prototype = "Autolathe", Count = 1, Placement = observed }];
        Assert.That(PressureEvaluator.Evaluate(source, state, final: true).Success, Is.EqualTo(succeeds));
    }

    [Test]
    public void InvalidEquipmentPlacementTokensCannotBeIgnored()
    {
        var source = ProgramTestData.Minimal(); source.Departments[0].Rooms[0].Contents = [Machine()];
        source.Departments[0].Rooms[0].Contents[0].Placement = "wherever";
        Assert.That(RequirementCompiler.Compile(source).Success, Is.False);
        source.Departments[0].Rooms[0].Contents[0].Placement = "boundary";
        var state = State(); state.Rooms[0].Contents = [new() { Id = "Fabricator", Prototype = "Autolathe", Count = 1, Placement = "wherever" }];
        Assert.That(PressureEvaluator.Evaluate(source, state).Diagnostics.Any(d => d.Code == "content-measurement"), Is.True);
    }
}
