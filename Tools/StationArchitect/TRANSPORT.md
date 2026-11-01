# Pneumatic shortcuts and mail

Transport is a separate underfloor layer. It does not turn private rooms into
walking corridors or connect their atmosphere compartments.

The default crew profile follows the physical hardware in Exo: named disposal
units `Resources/Maps/exo.yml#uid=5833` (To Engineering) and `#uid=16550` (To Atmos),
with actual disposal trunks, straight tubes and bends. The item-mail profile uses
`MailingUnit`, as in `Resources/Maps/sushi.yml#uid=87`. Its paired addresses are
serialized into `MailingUnit` and `Configuration` components.

`DisposalUnit` accepts both items and bodies. `MailingUnit` explicitly accepts
items and excludes entity-storage containers. Consequently, mail cannot satisfy
a crew walking connection. Use `kind: utility` for an item-delivery requirement:

```yaml
id: DeliverSupplies
from: Cargo/MailRoom
to: [Medical/MedicalStorage]
kind: utility
modes: [mail]
selection: all
priority: required
presence: ifPresent
weight: 8
```

Crew connectivity can instead allow `disposalShortcut`, optionally alongside
public halls or maintenance. No public walking route is silently substituted for
a connection that only allows transport.

## Hardware profiles

Project `settings.transportProfiles` controls prototype IDs, their local cardinal
exit directions, endpoint footprints, approach clearance, supporting-floor policy,
maximum length, supply-cable prototype and mail device-frequency range. It is portable project configuration,
independent of room and department names. The two defaults are `PneumaticCrew`
and `PneumaticMail`; each transport mode has one configured profile.

The default pipe types are `DisposalTrunk`, `DisposalPipe`, and `DisposalBend`.
Their local exits come from the actual prototypes: trunk South, straight
South/North, bend South/West. The builder rotates them to match the physical path.
Engine directions use South = (0,-1), East = (1,0), North = (0,1), West = (-1,0).

Each connection receives a dedicated bidirectional point-to-point line, with an
entry unit and trunk on the same floor tile at each end. Arrival at a single-exit
trunk reverses the incoming travel direction, causing the holder to exit into the
receiving unit according to the game's disposal systems. Dedicated lines avoid
inventing ambiguous junction routing or tag routers.

Mail discovery uses device-network broadcasts. Each dedicated mail pair receives
one deterministic numeric frequency shared by its two units, with no frequency
shared by another saved transport record. The default configurable range is
40000–65535, separate from the native station-wide mail frequency. Export writes
both numeric receive and transmit frequencies, so discovery offers the other end
of that physical line. Missing, mismatched or colliding frequencies fail
validation; edited and locked records remain available for explicit repair.

Tube segments may run under actual room floors, built corridors, and configured
structural floors. They never bridge unplated Space. `allowExteriorFloor: true`
permits already built exterior platforms or catwalk floors; it does not invent
a bridge across an empty gap. Separate lines keep a tile of clearance so native
tube connection rules cannot accidentally join them.

Terminals reserve real floor footprints before other machinery is placed and
retain free approach tiles. Underfloor segments remain separate from ordinary
equipment collision footprints. Edits to protected lines are retained, but a
broken trunk, bad rotation, unsupported segment or missing terminal invalidates
the connection proof.

## Validation and export limits

Connection measurements derive from the materialized terminal prototypes,
trunks, rotated exits and continuous supported segments. A saved `mode` label is
insufficient. Crew-capable terminal pairs also become shortcut edges in access
bypass checks, including indirect shortcuts through an adjoining room. Item mail
does not become a crew edge. Neither network becomes an open gas path.

Export emits the real unit, trunk and segment prototypes plus configured supply
wiring. Mail exports paired tag, target and target-list values. These are hardware
and connectivity plans; a working station still needs in-engine checks of power,
APC supply, pneumatic pressure, insertion/ejection behavior and server-specific
prototype capabilities. Custom profile exits and footprint declarations must
match the actual prototypes installed on that server.
