'use strict';
const byId = id => document.getElementById(id);
const scopeKinds = {department: 'Department', core: 'Department core', suite: 'Local suite', room: 'Individual room', installation: 'Installation'};
let catalog, visible = null, busy = false, resizeReady = false, zoom = 1, showLabels = true;

function text(tag, content, className) {
  const node = document.createElement(tag);
  node.textContent = content;
  if (className) node.className = className;
  return node;
}
function notice(message, kind = '') {
  const node = byId('notice');
  node.textContent = message;
  node.className = 'notice ' + kind;
}
function currentDepartment() { return catalog?.departments.find(d => d.id === byId('department').value); }
function currentScope() { return currentDepartment()?.scopes?.find(s => s.id === byId('test-scope').value); }
function inventoryLabel(physical, abstract = 0) {
  return `${physical} equipment${abstract ? ` · ${abstract} service demands` : ''}`;
}
function buttons() {
  const selected = Boolean(currentScope());
  byId('generate').disabled = busy || !selected;
  byId('resize').disabled = busy || !selected || !resizeReady;
  byId('new-seed').disabled = busy || !selected;
  byId('department').disabled = busy || !catalog;
  byId('test-scope').disabled = busy || !currentDepartment()?.scopes?.length;
  for (const id of ['seed','area','aspect']) byId(id).disabled = busy || !selected;
}
function clearSelection() {
  visible = null;
  resizeReady = false;
  byId('map').hidden = true;
  byId('map').removeAttribute('src');
  byId('empty').hidden = false;
  byId('empty').style.display = '';
  for (const id of ['svg-link','report-link','proposal-link']) byId(id).hidden = true;
  byId('current-size').textContent = '';
  byId('selection-title').textContent = 'Inventory';
  byId('features').replaceChildren();
  byId('inventory-stats').replaceChildren();
  byId('failures').replaceChildren(text('p','No layout has been generated.','muted'));
  byId('quality').replaceChildren(text('p','Evaluated separately from required constraints.','muted'));
  for (const id of ['pending','external','parent']) byId(id).replaceChildren();
  for (const id of ['pending-count','external-count','parent-count']) byId(id).textContent = '0';
  buttons();
  renderRooms();
}
function chooseDepartment() {
  const group = currentDepartment();
  const select = byId('test-scope');
  select.replaceChildren();
  const placeholder = text('option','Choose a local test…');
  placeholder.value = '';
  select.append(placeholder);
  if (!group) { clearSelection(); return; }
  const mode = group.organization?.mode || 'clustered';
  const descriptions = {
    clustered: 'A department intended to form a connected cluster.',
    distributed: 'A distributed collection. Its amenities or installations belong at separate useful station locations.',
    mixed: 'A department core with independently placed suites or rooms.'
  };
  byId('organization').textContent = descriptions[mode] || descriptions.clustered;
  for (const scope of group.scopes || []) {
    const option = text('option',`${scopeKinds[scope.kind] || 'Local test'}: ${scope.name}`);
    option.value = scope.id;
    select.append(option);
  }
  // Distributed owners have no whole-owner packing target. Require choosing
  // the actual amenity or installation before enabling size controls.
  select.value = mode === 'distributed' ? '' : (group.scopes?.[0]?.id || '');
  chooseScope();
}
function chooseScope() {
  clearSelection();
  const scope = currentScope();
  if (!scope) {
    byId('area').value = '';
    byId('aspect').value = 1;
    byId('bounds').textContent = '';
    byId('scope-description').textContent = 'Select one local unit; its parent group is not a room or a packing envelope.';
    byId('expected').textContent = `${currentDepartment()?.scopes?.length || 0} local tests available`;
    byId('summary').textContent = 'Station distribution and group-wide requirements are evaluated during integration.';
    notice('Choose a room, suite or installation to test. The whole collection is not packed together.');
    return;
  }
  byId('area').value = scope.bounds.preferred;
  byId('aspect').value = 1;
  byId('bounds').textContent = `This test: ${scope.bounds.min}–${scope.bounds.max} floor tiles. Preferred: ${scope.bounds.preferred}.`;
  byId('scope-description').textContent = `${scopeKinds[scope.kind] || 'Local test'} · ${scope.name}. Station placement is a separate step.`;
  byId('expected').textContent = `${scope.selectedRooms} rooms · ${inventoryLabel(scope.selectedFeatures,scope.abstractServices)} · ${scope.externalConnections} outside connections`;
  byId('summary').textContent = 'All selected rooms and equipment in this local scope will be included.';
  notice(`Ready to test ${scope.name}.`);
}
function stat(value, label) {
  const node = text('div','','stat');
  node.append(text('b',value),text('span',label));
  return node;
}
function renderRooms() {
  const list = byId('rooms');
  list.replaceChildren();
  const rooms = visible?.rooms || currentScope()?.rooms || [];
  const query = byId('room-filter').value.toLowerCase();
  byId('room-count').textContent = rooms.length;
  for (const room of rooms) {
    if (!(room.name+' '+room.id).toLowerCase().includes(query)) continue;
    const button = text('button','','room');
    button.type = 'button';
    button.dataset.room = room.id;
    button.append(text('strong',room.name));
    button.append(text('small',room.area === undefined ? 'Selected in this test' : `${room.area} tiles · ${inventoryLabel(room.physicalFeatures,room.abstractServices)}`));
    button.onclick = () => selectRoom(room.id);
    list.append(button);
  }
}
function svgDocument() { try { return byId('map').contentDocument; } catch { return null; } }
function selectRoom(id) {
  const room = visible?.rooms.find(r => r.id === id);
  if (!room) return;
  for (const node of byId('rooms').children) node.classList.toggle('active',node.dataset.room === id);
  const svg = svgDocument();
  if (svg) for (const node of svg.querySelectorAll('.room')) {
    node.classList.toggle('highlight',node.dataset.instance === id);
    node.classList.toggle('dim',node.dataset.instance !== id);
  }
  byId('selection-title').textContent = room.name;
  byId('summary').textContent = `${room.area} usable floor tiles · ${inventoryLabel(room.physicalFeatures,room.abstractServices)}`;
  byId('features').replaceChildren();
  if (!room.features.length) byId('features').append(text('p','No equipment declared for this room.','muted'));
  for (const feature of room.features) {
    const node = text('div','','feature');
    node.append(text('strong',feature.name));
    node.append(text('small',feature.prototype || feature.assembly || 'Service demand'));
    const status = feature.abstract ? 'No physical entity; service operation remains a separate check.'
      : feature.placed ? `Placed${feature.layer ? ' · '+feature.layer : ''}` : 'Not placed';
    node.append(text('small',status,feature.placed || feature.abstract ? '' : 'missing'));
    if (Array.isArray(feature.nativeParts) && feature.nativeParts.length > 1) {
      const parts = text('details','','feature-parts');
      parts.append(text('summary',`${feature.nativeParts.length} placed objects`));
      const list = text('ul','');
      for (const part of feature.nativeParts) {
        const item = text('li',part.prototype || part.id || 'Object');
        if (part.id) item.title = part.id;
        list.append(item);
      }
      parts.append(list);
      node.append(parts);
    }
    byId('features').append(node);
  }
}
function setLink(id,url) { byId(id).href = url; byId(id).hidden = false; }
function checkCard(check, failed = false) {
  const node = text('div','','check'+(failed ? ' bad' : ''));
  const name = visible?.rooms.find(r => r.id === check.owner)?.name || check.owner || check.declaredAt || check.type;
  node.append(text('strong',name || 'Requirement'));
  node.append(text('p',check.message || `${check.type}: ${check.target || check.id}`));
  return node;
}
const qualityMetrics = {
  'core.aspect_ratio': {label:'Core stretch'},
  'core.fill_ratio': {label:'Core floor coverage', percent:true},
  'circulation.fraction': {label:'Floor used by corridors', percent:true},
  'rooms.mean_fill_ratio': {label:'Average room floor coverage', percent:true}
};
function qualityValue(value, metric) {
  if (Number.isFinite(value)) {
    const percent = qualityMetrics[metric]?.percent;
    return `${Number((value * (percent ? 100 : 1)).toFixed(percent ? 1 : 2))}${percent ? '%' : ''}`;
  }
  return typeof value === 'object' ? JSON.stringify(value) : String(value);
}
function qualityView(quality, retained) {
  const panel = byId('quality');
  panel.replaceChildren();
  if (!quality) {
    panel.append(text('p','Layout quality has not been evaluated.','quality-status pending'));
    return;
  }
  const review = quality.status !== 'passes_profile';
  const prefix = retained ? 'Rejected proposal: ' : '';
  panel.append(text('p',prefix+(review ? 'Needs review against the layout profile.' : 'Meets the tested layout profile.'),'quality-status '+(review ? 'review' : 'pass')));
  if (Number.isFinite(quality.score)) panel.append(text('p',`Profile score: ${quality.score.toFixed(2)}`,'quality-check'));
  for (const check of quality.checks || []) {
    const label = qualityMetrics[check.metric]?.label || check.message || check.description || check.metric || check.id || 'Quality check';
    const measured = check.measured === undefined ? '' : ` · measured ${qualityValue(check.measured,check.metric)}`;
    const bounds = ['min','max'].filter(bound => Number.isFinite(check[bound]))
      .map(bound => `${bound} ${qualityValue(check[bound],check.metric)}`);
    const target = bounds.length ? ` · target ${bounds.join(', ')}` : '';
    const status = {satisfied:'Within target',needs_review:'Needs review'}[check.status]
      || (check.status ? check.status.replaceAll('_',' ') : check.violation ? 'Needs review' : 'Measured');
    panel.append(text('p',`${status}: ${label}${measured}${target}`,'quality-check'));
  }
  panel.append(text('p','Station distribution, complete furnishing programs and runtime gameflow remain unevaluated.','scope-note'));
}
function showReport(result) {
  visible = result.visible;
  resizeReady = result.canResize;
  const report = result.proposal.report;
  const verification = report.verification;
  const shown = visible.report.verification;
  const scope = visible.report.scope || currentScope();
  byId('empty').style.display = 'none';
  byId('map').hidden = false;
  if (byId('map').getAttribute('src') !== visible.svgUrl) { zoom = 1; byId('map').src = visible.svgUrl; }
  setLink('svg-link',visible.svgUrl);
  setLink('report-link',visible.reportUrl);
  byId('proposal-link').hidden = !result.retainedPrevious;
  if (result.retainedPrevious) setLink('proposal-link',result.proposal.reportUrl);
  const metrics = shown.metrics || {};
  const width = metrics.department_width, height = metrics.department_height;
  const actualAspect = metrics.department_aspect;
  let size = `${scope?.name || 'Visible test'} · ${metrics.department_floor ?? metrics.usable_floor ?? '—'} floor tiles`;
  if (Number.isFinite(width) && Number.isFinite(height)) size += ` · ${width} × ${height}`;
  if (Number.isFinite(actualAspect)) size += ` · actual ratio ${actualAspect.toFixed(2)}`;
  byId('current-size').textContent = size;
  if (result.retainedPrevious) notice('The requested change failed its local constraints. The previous passing layout is still shown.','bad');
  else if (result.accepted) notice(`${scope?.name || 'This test'} passed its local constraints. Check layout quality separately; station placement is still pending.`,'good');
  else notice('This local proposal needs repairs. It has not replaced a passing layout.','bad');
  const inventory = shown.inventory || {};
  byId('selection-title').textContent = 'Local inventory';
  byId('summary').textContent = `${result.retainedPrevious ? 'Retained layout' : 'Displayed layout'}: ${scopeKinds[scope?.kind] || 'local test'}${scope?.name ? ' · '+scope.name : ''}.`;
  byId('inventory-stats').replaceChildren(
    stat(`${inventory.realizedRooms ?? visible.rooms.length}/${inventory.selectedRooms ?? visible.rooms.length}`,'rooms placed'),
    stat(`${inventory.realizedFeatures ?? 0}/${inventory.selectedFeatures ?? 0}`,'equipment placed'));
  if (Number.isFinite(inventory.selectedNativeParts) && Number.isFinite(inventory.realizedNativeParts)) {
    byId('inventory-stats').append(stat(`${inventory.realizedNativeParts}/${inventory.selectedNativeParts}`,'objects placed'));
  }
  if (inventory.abstractServices) byId('inventory-stats').append(stat(inventory.abstractServices,'service demands'));
  byId('features').replaceChildren(text('p','Select a room to inspect its equipment and service demands.','muted'));
  renderRooms();
  byId('checks-title').textContent = result.retainedPrevious ? 'Rejected change: constraints' : 'Local constraint checks';
  byId('failures').replaceChildren();
  const failures = verification.hard_failures || [];
  if (!failures.length) byId('failures').append(text('p','Selected local constraints passed.','pass'));
  else for (const check of failures) byId('failures').append(checkCard(check,true));
  qualityView(verification.layoutQuality,result.retainedPrevious);
  const parent = (verification.pendingRequirements || []).filter(c => c.facet === 'owner_organization');
  const pending = (verification.pendingRequirements || []).filter(c => c.facet !== 'owner_organization');
  const external = verification.externalContracts || [];
  for (const [id, checks] of [['parent',parent],['pending',pending],['external',external]]) {
    byId(id+'-count').textContent = checks.length;
    byId(id).replaceChildren(...checks.map(c => checkCard(c)));
  }
  buttons();
}
async function generate(resize) {
  const scope = currentScope();
  if (busy || !scope || !byId('controls').reportValidity()) return;
  const payload = {department: byId('department').value, scope: scope.id,
    seed: Number(byId('seed').value), area: Number(byId('area').value), aspect: Number(byId('aspect').value)};
  busy = true;
  document.body.classList.add('busy');
  buttons();
  notice(resize ? 'Checking the resized local test…' : `Generating ${scope.name}…`);
  try {
    const response = await fetch(resize ? '/api/resize' : '/api/generate',
      {method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(payload)});
    const result = await response.json();
    if (!response.ok) throw new Error(result.error || 'The request could not complete.');
    showReport(result);
  } catch (error) { notice(error.message,'bad'); }
  finally { busy = false; document.body.classList.remove('busy'); buttons(); }
}
function applyView() {
  const doc = svgDocument(), svg = doc?.documentElement;
  if (!svg || svg.tagName.toLowerCase() !== 'svg') return;
  svg.style.width = `${zoom*100}%`;
  svg.style.height = 'auto';
  const labels = doc.querySelector('.labels');
  if (labels) labels.style.display = showLabels ? '' : 'none';
}
byId('controls').onsubmit = event => { event.preventDefault(); generate(false); };
byId('resize').onclick = () => generate(true);
byId('department').onchange = chooseDepartment;
byId('test-scope').onchange = chooseScope;
byId('room-filter').oninput = renderRooms;
byId('seed').onchange = () => { resizeReady = false; buttons(); notice('Seed changed. Generate this local test before resizing.'); };
byId('new-seed').onclick = () => { byId('seed').value = crypto.getRandomValues(new Uint32Array(1))[0]; byId('seed').onchange(); };
byId('zoom-in').onclick = () => { zoom = Math.min(6,zoom*1.25); applyView(); };
byId('zoom-out').onclick = () => { zoom = Math.max(.5,zoom/1.25); applyView(); };
byId('fit').onclick = () => { zoom = 1; applyView(); const doc = svgDocument(); if (doc) for (const node of doc.querySelectorAll('.room')) node.classList.remove('highlight','dim'); };
byId('labels').onclick = () => { showLabels = !showLabels; byId('labels').setAttribute('aria-pressed',showLabels); applyView(); };
byId('map').onload = applyView;
fetch('/api/catalog').then(async response => {
  const data = await response.json();
  if (!response.ok) throw new Error(data.error);
  return data;
}).then(data => {
  catalog = data;
  for (const group of data.departments) {
    const option = text('option',group.name);
    option.value = group.id;
    byId('department').append(option);
  }
  chooseDepartment();
}).catch(error => notice('Could not load the catalog: '+error.message,'bad'));
