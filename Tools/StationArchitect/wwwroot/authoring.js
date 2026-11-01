// Authoring operations are independent of both geometry and the first tool.
export const maintenanceId = '_maintenance';
export const clone = value => JSON.parse(JSON.stringify(value));
export const measurementKey = room => room.parentInstanceId ? `${room.parentInstanceId}/${room.path.split('/').at(-1)}#${room.instanceId}` : `${room.path}#${room.instanceId}`;
export const ownerList = document => [...document.departments, { ...document.maintenance, id:maintenanceId, name:'True maintenance' }];
export function roomEntries(document) {
    const result=[];
    function visit(owner,container,parentPath,depth=0){for(const room of container.rooms||[]){const path=parentPath+'/'+room.id;result.push({room,path,owner,ownerId:owner.id,container,parentPath,depth});visit(owner,room,path,depth+1);}}
    for(const owner of ownerList(document))visit(owner,owner,owner.id);
    return result;
}
export const roomPaths = document => roomEntries(document).map(entry=>entry.path);
export function locate(document, path) {
    if (path === '$') return { item:document, kind:'station', path };
    const [ownerId, ...roomIds] = path.split('/');
    const owner = ownerId === maintenanceId ? document.maintenance : document.departments.find(item => item.id === ownerId);
    if (!owner) return null;
    if (!roomIds.length) return { item:owner, kind:ownerId===maintenanceId?'maintenance':'department', ownerId, path };
    let container=owner,room,parentPath=ownerId;
    for(let index=0;index<roomIds.length;index++){room=container.rooms?.find(item=>item.id===roomIds[index]);if(!room)return null;if(index<roomIds.length-1){container=room;parentPath+='/'+roomIds[index];}}
    return { item:room, owner, ownerId, container, parentPath, kind:'room', path };
}
export function uniqueId(items, prefix) {
    let id=prefix, suffix=2;
    while(items.some(item => item.id === id)) id=prefix+suffix++;
    return id;
}
function visitConnections(document, action) {
    document.connections.forEach(rule => action(rule, null));
    for (const entry of roomEntries(document))
        entry.room.connections.forEach(rule => action(rule, entry.parentPath,entry.path));
}
function resolveReference(document,value,declaringPath){
    const known=new Set([...roomPaths(document),...ownerList(document).map(owner=>owner.id)]);
    if(known.has(value)||!declaringPath)return value;
    if(value==='..')return declaringPath.split('/').slice(0,-1).join('/');
    if(value.startsWith('./')||value.startsWith('../')){const parts=declaringPath.split('/');for(const part of value.split('/')){if(part==='..')parts.pop();else if(part!=='.')parts.push(part);}const path=parts.join('/');return known.has(path)?path:value;}
    const sibling=declaringPath.split('/').slice(0,-1).join('/')+'/'+value;
    if(known.has(sibling))return sibling;
    return known.has(declaringPath+'/'+value)?declaringPath+'/'+value:value;
}
export function renameItem(document, path, id) {
    if (!/^[A-Za-z0-9][A-Za-z0-9 _-]{0,63}$/.test(id)) throw new Error('IDs use 1–64 letters, numbers, spaces, underscores or hyphens, starting with a letter or number.');
    const node=locate(document,path);
    if (!node || node.kind==='maintenance') throw new Error('Select a department or room to rename.');
    if (node.kind==='station') {node.item.id=id;return '$';}
    const siblings=node.kind==='department'?document.departments:node.container.rooms;
    if (siblings.some(item => item!==node.item && item.id===id)) throw new Error('This ID is already used in the same scope.');
    const next=node.kind==='department'?id:`${node.parentPath}/${id}`;
    const map=value=>value===path?next:value.startsWith(path+'/')?next+value.slice(path.length):value;
    // Relative references belong to their declaring owner; references in other owners stay unchanged.
    visitConnections(document,(rule,containerPath,declaringPath)=>{
        if (rule.from) {const resolved=resolveReference(document,rule.from,declaringPath);if(map(resolved)!==resolved)rule.from=map(resolved);}
        rule.to=rule.to.map(target=>{
            const resolved=resolveReference(document,target,declaringPath),mapped=map(resolved);
            if(mapped===resolved)return target;
            if(!target.includes('/')&&resolved!==target&&resolved.split('/').at(-1)===mapped.split('/').at(-1))return target;
            return node.kind==='room'&&containerPath===node.parentPath&&target===node.item.id?id:mapped;
        });
    });
    for(const choice of document.choices||[]) choice.rooms=choice.rooms.map(map);
    if(node.kind==='department') for(const {room} of roomEntries(document))
        if(room.access.entryOwners)room.access.entryOwners=room.access.entryOwners.map(value=>value===path?next:value);
    node.item.id=id;
    return next;
}
export function removeItem(document,path) {
    const node=locate(document,path);
    if(!node || !['department','room'].includes(node.kind)) return false;
    if(node.kind==='department') document.departments=document.departments.filter(item=>item!==node.item);
    else node.container.rooms=node.container.rooms.filter(item=>item!==node.item);
    // Keep surviving dependencies visible. Deleting a declaration must not silently remove a required rule.
    return true;
}
export function reparentRoom(document,path,parentPath){
    const node=locate(document,path),parent=locate(document,parentPath);
    if(node?.kind!=='room'||!['room','department','maintenance'].includes(parent?.kind))throw new Error('Choose a room and a department or parent room.');
    if(parentPath===path||parentPath.startsWith(path+'/'))throw new Error('A room cannot contain itself or one of its ancestors.');
    if(parentPath===node.parentPath)return path;
    const next=parentPath+'/'+node.item.id;
    if((parent.item.rooms||[]).some(room=>room.id===node.item.id))throw new Error('The destination already contains a room with this ID.');
    const map=value=>value===path?next:value.startsWith(path+'/')?next+value.slice(path.length):value;
    // Resolve references before changing scopes. Surviving rules keep their original targets.
    visitConnections(document,(rule,containerPath,declaringPath)=>{
        const moved=declaringPath===path||declaringPath?.startsWith(path+'/');
        if(rule.from){const resolved=resolveReference(document,rule.from,declaringPath);if(moved||map(resolved)!==resolved)rule.from=map(resolved);}
        rule.to=rule.to.map(target=>{const resolved=resolveReference(document,target,declaringPath);return moved||map(resolved)!==resolved?map(resolved):target;});
    });
    for(const choice of document.choices||[])choice.rooms=choice.rooms.map(map);
    node.container.rooms=node.container.rooms.filter(room=>room!==node.item);
    (parent.item.rooms??=[]).push(node.item);
    return next;
}
export function duplicateRoom(document,path){
    const node=locate(document,path);if(node?.kind!=='room')throw new Error('Select a room to duplicate.');
    const room=clone(node.item),id=uniqueId(node.container.rooms,room.id+'Copy'),next=node.parentPath+'/'+id;
    const map=value=>value===path?next:value.startsWith(path+'/')?next+value.slice(path.length):value;
    function visit(item){for(const rule of item.connections){if(rule.from)rule.from=map(rule.from);rule.to=rule.to.map(map);}for(const child of item.rooms||[])visit(child);}
    visit(room);room.id=id;room.name=(room.name||node.item.id)+' copy';node.container.rooms.push(room);return next;
}
export function setField(item,path,value) {
    const keys=path.split('.');let target=item;
    for(const key of keys.slice(0,-1)) target=target[key]??= {};
    target[keys.at(-1)]=value;
}
export function programStats(document) {
    const entries=roomEntries(document),rooms=entries.map(entry=>entry.room);
    const required=entry=>entry.room.count.min*(entry.depth?required(entries.find(parent=>parent.path===entry.parentPath)):1);
    return {departments:document.departments.length, rooms:rooms.length,
        required:entries.reduce((sum,entry)=>sum+required(entry),0),
        optional:rooms.filter(room=>room.count.min===0&&room.role!=='filler').length,
        filler:rooms.filter(room=>room.role==='filler').length,
        connections:document.connections.length+rooms.reduce((sum,room)=>sum+room.connections.length,0)};
}
