# Existing-map layout evidence

`Analysis/layout-evidence.json` records actual main-grid floor geometry, beacon coordinates, entity UIDs and chapel door boundaries for Bagel, Box, Packed, Marathon and Saltern. Reproduce it from the repository root:

```powershell
python Tools/StationArchitect/Analysis/layout_evidence.py
```

The script decodes floor chunks and resolves prototype inheritance through the same audited helpers as `Analysis/analyze_maps.py`. Each map retains its source path and SHA-256. A closed-door component is a conservative geometric observation: directional windows/walls occupy whole cells and furniture does not obstruct the flood. It is not a verified gameplay route or an exact room census.

## Chapel complexes

Bagel (`Resources/Maps/bagel.yml`) provides explicit named evidence. WoodDoor **13851**, named **Confessional**, at **(-21.5,20.5)** connects the chapel's 77-tile component **118** to a two-tile booth **141**, containing wooden chair **14620**. WoodDoor **14518** is also named Confessional. Crematorium **11884** lies nearby at **(-18.5,15.5)** in component **139**.

Box has two enclosed one-tile chair booths **408/409**, reached from chapel **398** through wooden doors **10537/10538**. Packed similarly has one-tile comfy-chair booths **140/146**, through wooden doors **15327/12392**. These support separately bounded child rooms with configurable counts and sizes; a single chapel rectangle misses their internal structure.

Saltern's chapel **11** (20 floor tiles) reaches a ten-tile room **12** through chapel airlock **10439**; that room contains crematorium **7631**. Box's crematorium suite also has a named maintenance entrance **10761**. Parent-only access is a useful authored default, while secondary external entries must remain configurable to represent existing variants.

## Distributed common and service spaces

Map | Arrivals beacon | Cryosleep beacon | Evac beacon
--- | --- | --- | ---
Bagel | 18266: (0.5,-63.5) | 3128: (11.5,-15.5) | 25312: (46.5,24.5)
Box | 27710: (-64.5,2.5) | 9541: (24.5,18.5) | 14082: (81.5,-8.5)
Packed | 14028: (-23.5,-12.5) | 15113: (21.5,18.5) | 4139: (-7.5,-0.5)

Cryosleep and evacuation are distinct destinations spread across these stations. Their position does not follow automatically from the Arrivals department. Medical **Cryopods** are separate labels and should not be confused with passenger cryosleep.

Some service functions share open room geometry: Bagel's Kitchen **23370** and Botany **16140** share closed-door component **285**; Saltern's Kitchen **11318** and Botany **11320** share component **59**. Other service destinations are separated: Bagel's Chapel **13601** and Library **23375** are around y=18, while its Bar **16141** is at y=-33.5. This supports grouped local complexes within a distributed department rather than forcing every service room into one contiguous owner block.

## Compactness and maintenance interpretation

Decoded main-grid floor occupies 43–53% of each selected map's floor bounding rectangle. This includes station wings and docking structures; it is not a target fill ratio for departments or proof that the remainder is maintenance. Empty cells are actual Space until floor is built.

The chapel observations include true local maintenance passages: Packed component **132** has eight floor tiles in an **8×1** conservative closed-door estimate, and Bagel components **94/106** are long **4/3**-tile-deep floor regions around the chapel. Directional doors and windows distort these component estimates. The generator's authored 2–3-wide maintenance rule therefore remains explicit rather than being inferred as a universal measured map width. Maintenance coverage is measured against actual cluster boundary edges and physically built nearby passage floor; room-sized leftover voids do not count.

The source observations establish layout patterns and starting values. They do not establish live SS14 atmosphere, access, evacuation or machine operation; those still require game-side verification of exported drafts.
