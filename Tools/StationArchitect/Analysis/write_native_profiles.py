"""Rebuild the native hardware profile library from this checkout's prototypes.

Run from any directory. Requires Python 3 and PyYAML. --check compares the
committed library without writing it. Native nodes/inheritance and all source
hashes are audited; no running-game or sustained resource operation is claimed.
"""
import argparse
import json
from pathlib import Path
from native_system_sources import audit_profiles, evidence, native_occupancy, native_carries_crew, prototypes

def build(repo):
    protos = prototypes(repo)
    profiles = []

    def port(id, kind, connection, group, dirs=(), layer=0, range=0, volume=0):
        return dict(id=id, kind=kind, connection=connection, nodeGroup=group, directions=list(dirs), pipeLayer=layer, range=range, volumeLitres=volume)

    def profile(id, proto, kind, ports, required=(), transfers=(), sources=(), power=False, components=None):
        c = protos[proto]['components']
        missing = set(required) - set(c)
        if missing:
            raise ValueError(f'{id}: missing native required components {sorted(missing)}')
        value = dict(id=id, prototype=proto, kind=kind, ports=ports, requiredComponents=list(required), transfers=list(transfers), sources=list(sources), requiresPower=power, components=components or {}, sourceRefs=[protos[proto]['sourcePath']])
        value.update(native_occupancy(c))
        value['carriesCrew'] = native_carries_crew(c)
        profiles.append(value)
        return value

    def transfer(a, b):
        return dict(fromPort=a, toPort=b)

    def receiver():
        return port('power', 'power', 'rangeReceiver', 'Apc', range=3)

    def provider():
        return port('provider', 'power', 'rangeProvider', 'Apc', range=3)

    def src(portid, energy):
        return dict(portId=portid, kind='power', storedEnergyJoules=energy)
    for (id, proto, group) in [('hv-cable', 'CableHV', 'HVPower'), ('mv-cable', 'CableMV', 'MVPower'), ('lv-cable', 'CableApcExtension', 'Apc')]:
        profile(id, proto, 'cable', [port('power', 'power', 'cable', group)] + ([provider()] if group == 'Apc' else []), ['NodeContainer'] + (['PowerProvider', 'ExtensionCableProvider'] if group == 'Apc' else []), [transfer('power', 'provider')] if group == 'Apc' else [])
    profile('cable-terminal', 'CableTerminal', 'cable', [port('powerHV', 'power', 'terminal', 'HVPower', ['South']), port('powerMV', 'power', 'terminal', 'MVPower', ['South'])], ['NodeContainer'])
    for (proto, energy) in [('SMESBasic', 8000000), ('SMESAdvanced', 16000000)]:
        profile(proto.lower(), proto, 'device', [port('input', 'power', 'terminalPort', 'HVPower'), port('output', 'power', 'cableDevice', 'HVPower')], ['NodeContainer', 'Battery', 'BatteryCharger', 'BatteryDischarger', 'PowerNetworkBattery'], [transfer('input', 'output')], [src('output', energy)], components={'Battery': {'startingCharge': energy, 'maxCharge': energy}})
    profile('substation', 'SubstationBasic', 'device', [port('input', 'power', 'cableDevice', 'HVPower'), port('output', 'power', 'cableDevice', 'MVPower')], ['NodeContainer', 'Battery', 'BatteryCharger', 'BatteryDischarger', 'PowerNetworkBattery'], [transfer('input', 'output')], [src('output', 2500000)], components={'Battery': {'startingCharge': 2500000, 'maxCharge': 2500000}})
    profile('apc', 'APCBasic', 'device', [port('input', 'power', 'cableDevice', 'MVPower'), port('output', 'power', 'cableDevice', 'Apc'), provider()], ['NodeContainer', 'Battery', 'BatteryCharger', 'PowerNetworkBattery', 'Apc', 'PowerProvider', 'ExtensionCableProvider'], [transfer('input', 'output'), transfer('output', 'provider')], [src('output', 50000)], components={'Battery': {'startingCharge': 50000, 'maxCharge': 50000}})
    for (layer, suffix) in [(0, ''), (1, 'Alt1')]:
        for (id, proto, dirs, volume) in [('half', 'GasPipeHalf', ['South'], 100), ('straight', 'GasPipeStraight', ['South', 'North'], 200), ('bend', 'GasPipeBend', ['South', 'West'], 200), ('tee', 'GasPipeTJunction', ['South', 'East', 'West'], 300), ('cross', 'GasPipeFourway', ['South', 'East', 'North', 'West'], 400)]:
            name = proto + suffix
            nodes = protos[name]['components'].get('NodeContainer', {}).get('nodes', {})
            native = nodes.get('pipe', {})
            volume = native.get('volume', 200)
            profile('gas-' + str(layer) + '-' + id, name, 'pipe', [port('pipe', 'gas', 'pipe', 'Pipe', dirs, layer=layer, volume=volume)], ['NodeContainer', 'AtmosPipeLayers'])
        profile('gas-' + str(layer) + '-connector', 'GasPort' + suffix, 'pipe', [port('connected', 'gas', 'pipePort', 'Pipe', ['South'], layer=layer, volume=200)], ['NodeContainer', 'GasPort', 'AtmosPipeLayers'])
    for (id, proto, layer) in [('vent', 'GasVentPump', 0), ('scrubber', 'GasVentScrubberAlt1', 1)]:
        profile(id, proto, 'device', [port('pipe', 'gas', 'pipe', 'Pipe', ['South'], layer=layer, volume=200), receiver()], ['NodeContainer', 'ApcPowerReceiver', 'ExtensionCableReceiver', 'AtmosDevice', 'DeviceNetwork', 'GasVentPump' if id == 'vent' else 'GasVentScrubber'], power=True)
    air = protos['AirCanister']['components']['GasCanister']['air']
    moles = air['moles']
    total = sum(moles.values())
    pressure = total * 8.314462618 * air['temperature'] / air['volume']
    profile('air-reserve', 'AirCanister', 'device', [port('port', 'gas', 'portable', 'Pipe', volume=1)], ['NodeContainer', 'GasPortable', 'GasCanister'], sources=[dict(portId='port', kind='gas', volumeLitres=air['volume'], pressureKpa=pressure, temperatureKelvin=air['temperature'], gasFractions={k: v / total for (k, v) in moles.items()})], components={'GasCanister': {'air': air}, 'Physics': {'bodyType': 'Static'}})
    profile('scrub-reserve', 'StorageCanister', 'device', [port('port', 'gas', 'portable', 'Pipe', volume=1)], ['NodeContainer', 'GasPortable', 'GasCanister'], sources=[dict(portId='port', kind='gasSink', volumeLitres=1500, pressureKpa=0, temperatureKelvin=293.15)], components={'GasCanister': {'air': {'volume': 1500, 'temperature': 293.15, 'moles': {}}}, 'Physics': {'bodyType': 'Static'}})
    for (id, proto) in [('light', 'Poweredlight'), ('air-alarm', 'AirAlarm'), ('fire-alarm', 'FireAlarm'), ('air-sensor', 'AirSensor')]:
        profile(id, proto, 'device', [receiver()], ['ApcPowerReceiver', 'ExtensionCableReceiver'] + {'Poweredlight': ['PoweredLight', 'DeviceNetwork'], 'AirAlarm': ['AirAlarm', 'AtmosAlarmable', 'DeviceList', 'DeviceNetwork'], 'FireAlarm': ['FireAlarm', 'AtmosAlarmable', 'DeviceList', 'DeviceNetwork'], 'AirSensor': ['DeviceNetwork', 'AtmosMonitor']}[proto], power=True)
    profile('firelock', 'Firelock', 'device', [receiver()], ['Door', 'Firelock', 'AtmosAlarmable', 'DeviceNetwork', 'DeviceNetworkRequiresPower', 'ApcPowerReceiver', 'ExtensionCableReceiver'], power=True)
    for proto in ['Airlock', 'AirlockExternal', 'AirlockShuttle', 'AirlockExternalGlassShuttleArrivals', 'AirlockExternalGlassShuttleEmergencyLocked', 'AirlockExternalGlassShuttleCargo']:
        profile('door-' + proto.lower(), proto, 'device', [receiver()], ['Door', 'ApcPowerReceiver', 'ExtensionCableReceiver'], power=True)
    profile('waste-unit', 'DisposalUnit', 'device', [receiver(), port('entry', 'disposal', 'disposalUnit', 'Disposal')], ['DisposalUnit', 'ApcPowerReceiver', 'ExtensionCableReceiver'], power=True)
    for (id, proto) in [('trunk', 'DisposalTrunk'), ('straight', 'DisposalPipe'), ('bend', 'DisposalBend'), ('junction', 'DisposalJunction'), ('junction-flipped', 'DisposalJunctionFlipped'), ('y-junction', 'DisposalYJunction')]:
        exits = protos[proto]['components']['DisposalTube'].get('exits', ['South'])
        profile('waste-' + id, proto, 'disposal', [port('tube', 'disposal', 'disposalEntry' if id == 'trunk' else 'disposal', 'Disposal', exits)], ['DisposalTube'] + (['DisposalEntry'] if id == 'trunk' else []))
    result = {'profiles': profiles, 'evidence': {'simulationVerified': False, 'notes': ['Finite native stored energy and gas only; no AME/TEG startup or sustained gas production is asserted.', 'Air bank12 uses33232.2mol in18000L; native200L-per-segment volume must be added when estimating diluted initial pressure.', 'GasPort is an underfloor native connector segment at the same tile as its anchored canister.', 'Native CableTerminal rotation0 points South, as Direction.South.ToAngle() is zero.'], 'airCanister': {'moles': moles, 'volumeLitres': air['volume'], 'temperatureKelvin': air['temperature'], 'pressureKpa': pressure}, 'sourcePaths': ['Content.Server/Power/Nodes/CableDeviceNode.cs', 'Content.Server/Power/Nodes/CableTerminalNode.cs', 'Content.Server/Power/Nodes/CableTerminalPortNode.cs', 'Content.Server/NodeContainer/Nodes/PipeNode.cs', 'Content.Server/NodeContainer/Nodes/PortPipeNode.cs', 'Content.Server/NodeContainer/Nodes/PortablePipeNode.cs', 'Content.Server/Atmos/Piping/Unary/EntitySystems/GasCanisterSystem.cs', 'Content.Shared/DeviceConfigurator/Components/DeviceListComponent.cs', 'Content.Shared/Disposal/Tube/DisposalTubeSystem.cs', 'Content.Shared/Disposal/Unit/SharedDisposalUnitSystem.cs']}}
    errors = audit_profiles(protos, profiles)
    if errors:
        raise ValueError('Native profile audit failed:\n' + '\n'.join(errors))
    result['evidence'].update(evidence(repo, protos, [p['prototype'] for p in profiles]))
    result['evidence']['generator'] = 'Tools/StationArchitect/Analysis/write_native_profiles.py'
    return result
if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--repo', type=Path, default=Path(__file__).resolve().parents[3])
    parser.add_argument('--output', type=Path, help='Repo-relative output, or an absolute path')
    parser.add_argument('--check', action='store_true', help='Fail on any committed artifact drift; do not write')
    args = parser.parse_args()
    repo = args.repo.resolve()
    output = args.output or Path('Tools/StationArchitect/Catalog/native-profiles.json')
    if not output.is_absolute():
        output = repo / output
    result = build(repo)
    expected = json.dumps(result, indent=2) + '\n'
    if args.check:
        if not output.is_file() or output.read_text(encoding='utf-8') != expected:
            raise SystemExit('Native profile artifact differs; rerun this script without --check')
    else:
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(expected, encoding='utf-8')
    print(json.dumps({'profiles': len(result['profiles']), 'sourceHashes': len(result['evidence']['sourceHashes']), 'output': str(output), 'checked': args.check, 'simulationVerified': False}))
