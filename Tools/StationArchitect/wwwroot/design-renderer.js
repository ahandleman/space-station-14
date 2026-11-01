export const escapeHtml=value=>String(value??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const esc=escapeHtml;
export function flattenTree(root){return root?[root,...(root.children||[]).flatMap(flattenTree)]:[];}
export function findTreeNode(root,id){return flattenTree(root).find(node=>node.id===id);}
export function definitionAt(document,path){const [ownerId,...roomIds]=path.split('/');let node=ownerId==='_maintenance'?document.maintenance:document.departments.find(department=>department.id===ownerId);for(const id of roomIds)node=node?.rooms?.find(room=>room.id===id);return node;}
export function treeHtml(project,selected,expanded,query='',issuesOnly=false){
    const colors=new Map(project.definition.departments.map(owner=>[owner.id,owner.color]));colors.set('_maintenance','#8c9770');
    const hasError=node=>(node.diagnostics||[]).some(diagnostic=>diagnostic.severity==='error')||(node.children||[]).some(hasError);
    const matches=node=>!query||`${node.id} ${node.name}`.toLowerCase().includes(query)||(node.children||[]).some(matches);
    function row(node,depth){
        if(!matches(node)||issuesOnly&&!hasError(node))return '';
        const children=node.children||[],open=expanded.has(node.id)||query||selected.startsWith(node.id+'/')||selected.startsWith(node.id+'#');
        const color=colors.get(node.definitionPath?.split('/')[0])||'#b6d9c4';
        const metric=node.kind==='instance'?`${node.floorArea}t`:node.kind==='template'?`×${children.filter(child=>child.kind==='instance').length}`:`${node.floorArea}t`;
        let html=`<button class="design-tree-row ${node.id===selected?'selected':''} ${hasError(node)?'has-error':''} ${node.pinned?'pinned':''} ${node.orphaned?'orphaned':''}" style="padding-left:${7+depth*12}px" data-node="${esc(node.id)}"><span class="tree-expand" ${children.length?`data-expand="${esc(node.id)}"`:''}>${children.length?(open?'▾':'▸'):'·'}</span><i class="tree-mark" style="background:${/^#[a-f0-9]{6}$/i.test(color)?color:'#8c9770'}"></i><span class="tree-label">${esc(node.name||node.id)}</span><span class="tree-count">${esc(metric)}${node.locked?' L':''}</span></button>`;
        if(open)html+=children.map(child=>row(child,depth+1)).join('');
        return html;
    }
    return project.tree?row(project.tree,0):'<p class="empty">Resolve the room selection to build its runtime tree.</p>';
}
export function cellsSvg(cells,color,name,opacity=1,attributes=''){
    const rows=new Map();for(const point of cells||[]){if(!rows.has(point.y))rows.set(point.y,new Set());rows.get(point.y).add(point.x);}
    let html=`<g data-layer="${esc(name)}" fill="${esc(color)}" opacity="${opacity}" ${attributes}>`;
    for(const [y,set] of rows){const xs=[...set].sort((a,b)=>a-b);for(let index=0;index<xs.length;index++){const first=xs[index];let last=first;while(index+1<xs.length&&xs[index+1]===last+1)last=xs[++index];html+=`<rect x="${first}" y="${y}" width="${last-first+1}" height="1"/>`;}}
    return html+'</g>';
}
export function allDesignCells(project,clearances=true){const layout=project.layout;return [...layout.envelope,...layout.rooms.flatMap(room=>room.cells),...layout.publicCorridors,...layout.maintenance,...layout.equipment.map(entity=>entity.position),...(clearances?(layout.docks||[]).flatMap(dock=>[...dock.boundaryCells,...dock.berthCells,...dock.approachCells]):[])];}
export function designSvg(project,{selected='$station',stage='placement',labels=true,equipment=true,utilities=true,clearances=true}={}){
    const layout=project.layout,colors=new Map(project.definition.departments.map(owner=>[owner.id,owner.color]));colors.set('_maintenance','#87946e');
    const early=['program','selection','placement'].includes(stage);
    let html='<defs><pattern id="design-grid" width="1" height="1" patternUnits="userSpaceOnUse"><path d="M1 0H0V1" fill="none" stroke="#aec7c1" stroke-opacity=".1" stroke-width=".04"/></pattern><pattern id="reserved-hatch" width="2" height="2" patternUnits="userSpaceOnUse"><path d="M0 2L2 0" stroke="#bb8753" stroke-width=".2"/></pattern></defs>';
    html+=cellsSvg(layout.envelope,'#203840','envelope',.55,'pointer-events="none"');
    if(early)for(const owner of layout.departments){const b=owner.bounds,color=colors.get(owner.ownerId)||'#87946e';html+=`<rect x="${b.x}" y="${b.y}" width="${b.width}" height="${b.height}" fill="${esc(color)}" fill-opacity=".07" stroke="${esc(color)}" stroke-opacity=".6" stroke-width=".22" stroke-dasharray="1.2 1" data-owner="${esc(owner.ownerId)}"><title>${esc(owner.ownerId)} planning region</title></rect>`;}
    if(!early)for(const room of layout.rooms){if(!room.cells.length)continue;const color=colors.get(room.ownerId)||'#bb7777';html+=cellsSvg(room.cells,color,'room-'+room.id,room.orphaned ? .35 : .83,`data-room="${esc(room.id)}"`);}
    html+=cellsSvg(layout.publicCorridors,'#d2d9cc','public halls',1,'pointer-events="none"');
    html+=cellsSvg(layout.maintenance,'#7e8760','maintenance',1,'pointer-events="none"');
    html+=cellsSvg(layout.walls,'#33444d','walls',1,'pointer-events="none"');
    html+=cellsSvg(layout.hull,'#5f7278','hull',1,'pointer-events="none"');
    html+=cellsSvg(layout.windows,'#72b4cd','windows',.9,'pointer-events="none"');
    const dockingSpace=new Set((layout.docks||[]).flatMap(dock=>[...dock.berthCells,...dock.approachCells]).map(cell=>cell.x+','+cell.y));
    html+=cellsSvg(layout.reservedSpace.filter(cell=>!dockingSpace.has(cell.x+','+cell.y)),'url(#reserved-hatch)','space reservations',.8,'pointer-events="none"');
    if(clearances){
        html+='<defs><marker id="dock-arrow" viewBox="0 0 10 10" refX="8" refY="5" markerWidth="4" markerHeight="4" orient="auto-start-reverse"><path d="M0 0L10 5L0 10Z" fill="#79d5e7"/></marker></defs>';
        for(const dock of layout.docks||[]){
            html+=cellsSvg(dock.berthCells,'#65b7d2','dock-berth-'+dock.id,.2,'pointer-events="none"');
            html+=cellsSvg(dock.approachCells,'#65b7d2','dock-approach-'+dock.id,.08,'pointer-events="none"');
            html+=cellsSvg(dock.boundaryCells,'#c4f4ff','dock-boundary-'+dock.id,.9,`data-room="${esc(dock.roomId)}"`);
            if(!dock.boundaryCells.length||!dock.berthCells.length)continue;
            const center=cells=>({x:cells.reduce((sum,cell)=>sum+cell.x+.5,0)/cells.length,y:cells.reduce((sum,cell)=>sum+cell.y+.5,0)/cells.length});
            const start=center(dock.berthCells),end=center(dock.approachCells.length?dock.approachCells:dock.berthCells),label=center(dock.berthCells);
            html+=`<path d="M${start.x} ${start.y}L${end.x} ${end.y}" fill="none" stroke="#79d5e7" stroke-width=".2" stroke-dasharray="1 .6" marker-end="url(#dock-arrow)" pointer-events="none"><title>${esc(dock.roomId+' / '+dock.requirementId)}: empty shuttle berth and approach</title></path>`;
            if(labels)html+=`<text x="${label.x}" y="${label.y}" text-anchor="middle" fill="#9edfe9" font-size="1.1" pointer-events="none">${esc(dock.requirementId)} · empty berth</text>`;
        }
        if(early)for(const reservation of layout.circulationReservations||[])html+=cellsSvg(reservation.approachCells,reservation.network==='publicHall'?'#d2d9cc':'#9ca86e','planned-approach-'+reservation.id,.16,'pointer-events="none"');
    }
    for(const port of layout.interfaces){const color=port.kind==='interaction'?'#73bed6':port.kind==='utility'?'#f0b865':'#83d9b6';html+=cellsSvg(port.cells,color,'interface-'+port.id,1,`data-interface="${esc(port.id)}"`);}
    if(utilities&&!early)for(const utility of layout.utilities){if(!utility.cells.length)continue;html+=`<path d="${utility.cells.map((point,index)=>`${index?'L':'M'}${point.x+.5} ${point.y+.5}`).join('')}" fill="none" stroke="${utility.type==='pipe'?'#e4b861':'#79aee2'}" stroke-width=".19" stroke-opacity=".85" pointer-events="none"><title>${esc(utility.type+' · '+utility.requirementId)}</title></path>`;}
    if(utilities&&!early)for(const network of layout.transportNetworks||[]){if(!network.cells.length)continue;html+=`<path d="${network.cells.map((point,index)=>`${index?'L':'M'}${point.x+.5} ${point.y+.5}`).join('')}" fill="none" stroke="${network.mode==='mail'?'#b8a5e6':'#e7a3c6'}" stroke-width=".25" stroke-dasharray=".8 .35" pointer-events="none"><title>${esc(network.mode+' ? '+network.requirementId+' ? underfloor transport')}</title></path>`;for(const terminal of network.terminals)html+=cellsSvg(terminal.footprint,'#d3a7df','terminal-'+terminal.id,.9,`pointer-events="none"`);}
    if(equipment&&!early)for(const entity of layout.equipment){const footprint=entity.footprint?.length?entity.footprint:[entity.position];html+=cellsSvg(footprint,entity.pinned?'#f7dd9b':'#d0b47d','equipment-'+entity.id,.9,`data-equipment="${esc(entity.id)}"`);html+=`<rect x="${entity.position.x+.12}" y="${entity.position.y+.12}" width=".76" height=".76" fill="none" stroke="#413423" stroke-width=".12" data-equipment="${esc(entity.id)}"><title>${esc(entity.prototype+' · '+entity.roomId)}</title></rect>`;}
    for(const room of layout.rooms){
        if(!room.hasSeed&&!room.cells.length)continue;
        const b=room.cells.length?room.bounds:room.parcel,point=room.cells.length?{x:b.x+b.width/2,y:b.y+b.height/2}:room.seed;
        const chosen=selected===room.id||selected===room.definitionPath||selected===room.ownerId||room.id.startsWith(selected+'/')||room.definitionPath.startsWith(selected+'/');
        if(early)html+=`<circle cx="${room.seed.x+.5}" cy="${room.seed.y+.5}" r="${chosen ? .8 : .5}" fill="${esc(colors.get(room.ownerId)||'#87946e')}" stroke="${chosen?'#effff4':'#1b2c33'}" stroke-width=".22" data-room="${esc(room.id)}"><title>${esc(room.id)}${room.pinned?' · pinned':''}</title></circle>`;
        if(chosen&&room.cells.length)html+=`<rect x="${b.x-.2}" y="${b.y-.2}" width="${b.width+.4}" height="${b.height+.4}" fill="none" stroke="#e1fff0" stroke-width=".22" stroke-dasharray=".8 .5" pointer-events="none"/>`;
        if(labels&&(chosen||!early||stage==='placement'))html+=`<text x="${point.x}" y="${point.y}" text-anchor="middle" dominant-baseline="middle" fill="${chosen?'#ffffff':'#d7e5da'}" font-size="${early?1.2:Math.max(.9,Math.min(1.55,b.width/Math.max(6,room.definitionPath.split('/').at(-1).length)*1.4))}">${esc(room.definitionPath.split('/').at(-1)+(Number(room.instance)>1?' '+room.instance:''))}</text>`;
    }
    if(labels)for(const owner of layout.departments){const b=owner.bounds;html+=`<text x="${b.x+b.width/2}" y="${b.y-1.8}" text-anchor="middle" fill="${esc(colors.get(owner.ownerId)||'#a3bc96')}" font-size="2.3" font-weight="600">${esc(owner.ownerId)}</text>`;}
    const points=allDesignCells(project,clearances);if(points.length){let minX=Infinity,minY=Infinity,maxX=-Infinity,maxY=-Infinity;for(const p of points){minX=Math.min(minX,p.x);minY=Math.min(minY,p.y);maxX=Math.max(maxX,p.x);maxY=Math.max(maxY,p.y);}html+=`<rect x="${minX}" y="${minY}" width="${maxX-minX+1}" height="${maxY-minY+1}" fill="url(#design-grid)" pointer-events="none"/>`;}
    return html;
}
