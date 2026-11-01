# Department access-policy validation review

This review records the connector correction at that stage. Its Security cell
references predate the [current custody policy](../CATALOG.md#configurable-choices-and-deliberate-policy),
which replaces individual cells with Sentencing lockers and a shared Brig.
The original review is retained below; current catalog tests cover the revised
selected room inventory and access rules.

This pass reviews the existing local relationship declarations against their
room access policies. It adds no claim of fresh map inspection or native runtime
verification. The trigger was exercising complete catalog departments rather
than the small geometric demonstration fixture.

The previous generic `staff_traffic` rule prohibited passage between any two
entrances of most staff rooms. That is appropriate for protected destinations,
but conflicts with several rooms that the same catalog explicitly uses as
connectors. Native credentials and required approaches remain unchanged; each
correction below stays in the room that owns the policy.

## Explicit connector corrections

| Room | Existing local obligations | Correction and retained limits |
| --- | --- | --- |
| Medical treatment | Mandatory crew borders to triage, surgery and cryogenics; an internal Medical circulation route. | Permit controlled through movement. Public entry remains forbidden, and Medical door credentials remain required. |
| Medical morgue | Medical-only-area approach plus Detective/Chaplain approaches avoiding clinical rooms; every routine entrance has a Medical OR Detective OR Chapel interface policy. | Permit controlled movement between clinical and nonclinical receiving entrances. This does not grant uncredentialed access or make unrelated upstream clinical doors passable. |
| Security brig | Required cell doors, required warden connection and a secure circulation route. | Permit circulation through the Brig. Cells, Perma and the Armory retain terminal/controlled-entry policies. |
| Hospitality kitchen | A required cold-store door plus a staff route that avoids bar commons. | Permit kitchen staff movement to cold storage. Customer counters remain non-walking interfaces and public entry remains forbidden. |
| Command bridge | Required Captain-room border and Command circulation route. | Permit Command staff transit through the Bridge. The Captain room remains a destination, not a shortcut. |
| Cargo warehouse | Required loading-bay door and internal Cargo route. | Permit freight/staff movement between the two. Existing restricted credentials remain in place. |
| Cargo loading bay | Required warehouse border, real docking interface, berth and exterior approach. | Permit transit and exterior entry. The previous `allowExteriorEntry: false` contradicted its required dock. Uncredentialed entry remains forbidden. |
| Cargo Salvage | Internal Cargo access plus a required Salvage-airlock border. | Permit staff transit to EVA without using the loading bay as the airlock substitute. |
| Salvage airlock | Only interior neighbor is Salvage; required exterior door and pressure-cycle/suited-egress contract. | Permit passage through the vestibule. Interior neighbor and cycling restrictions remain unchanged. |
| Maintenance exterior vestibules | Only interior entrance is maintenance; required exterior airlock. | Permit maintenance-to-space transit. No public entrance is introduced. |

These are ten specific connector policies, not a general relaxation of staff
rooms. Surgery, Cryogenics, Cold Storage, Warden's Office, Captain's Room,
Armory, cells and deep maintenance stores still reject through traffic. A layout
algorithm must route through the authorized connector and avoid adding a redundant
second corridor door to a protected leaf merely to satisfy a route it can already
reach through its parent component.

`throughTraffic: true` is permission, not a requirement to add doors. It never
overrides `entryFrom`, public/maintenance/exterior entry flags, interface credentials,
or `mustTraverse`. For example, the Salvage airlock still cannot gain a hallway
entrance, and deep maintenance still requires maintenance traversal on every
relevant approach path.

## Morgue credential audit

`morgue_door` retains three alternative credential clauses:
`[[Medical], [Detective], [Chapel]]`. They are OR alternatives, not a combined
credential requirement and not public access. The local
`x_entry_interface_policy` continues to require that profile on every routine
entrance and preserves upstream credential checks. The Detective and Chaplain
routes still avoid treatment, surgery, chemistry, cryogenics, Medical circulation
and the other listed clinical spaces. Triage is deliberately not on that avoid
list, so a single receiving entrance via triage remains an allowed realization;
multiple role-controlled receiving entrances are also permitted.

The supporting native/map evidence remains in
[additional-medical-service.md](additional-medical-service.md). This review changes
the authored circulation policy, not that evidence or native access levels.

## Thin HoP frontage and normal counts

HoP's queue now has exclusive area 3–8 tiles, a mandatory one-tile short side,
one connected component and an explicit open edge directly onto the public hall.
Its route uses width one; the hall keeps its own larger clear width. The controlled
office counter and separate staff access remain local personnel-suite requirements.

The small fixture's Command aggregate bounds were adjusted to account for replacing
a waiting room by that strip. The standard catalog retains its department bounds;
its conservative selected-area analysis remains valid with the smaller queue.
Normal catalog Hospitality has `min = preferred = max = 1`. The two-suite
`hierarchy-station.yaml` remains an explicit repeated-binding test fixture, while
`station-layout.yaml` contains one normal hospitality suite.

The confessional's internal route is one tile wide. It is a one-person destination
with 2–10 usable tiles and a chair, not a through corridor. Its parent-only entry
and rejection of through traffic remain mandatory. The Chapel's public approach
and the adjacent Crematorium's internal route retain their two-tile clear width.

The focused checks in `tests/test_catalog_department_policies.py` preserve both the
connector permissions and the protected-leaf restrictions. Department resizing
must still pass the geometric and route checks; these YAML edits do not certify
feature placement, machinery operation or a complete station.
