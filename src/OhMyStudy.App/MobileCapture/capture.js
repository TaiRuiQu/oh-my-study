import {detectPaperCorners,mapPaperCorners} from './paper-detector.js';

const {strings,language} = JSON.parse(document.getElementById('capture-configuration').textContent);
const t = value => strings[value] || value;
const format = (value, ...args) => args.reduce((text, arg, index) => text.replaceAll('{'+index+'}', arg), t(value));
const $ = id => document.getElementById(id);
document.documentElement.lang = language;
document.querySelectorAll('[data-text]').forEach(element => element.textContent = t(element.dataset.text));
document.querySelectorAll('[data-aria]').forEach(element => element.setAttribute('aria-label', t(element.dataset.aria)));
document.title = t('Take photos with your phone') + ' · Oh My Study';
const token = location.hash.slice(1);
const items = [], controls = [];
let view = 'capture', active = null, corners = [], rotation = 0, editing = false, automaticCrop = true;
let reviewIndex = 0, reviewLayout = 'image', filterOpen = false, showOriginal = false;
let multiCapture = true, highResolution = false, defaultEnhancement = 'original';
let defaultBackend = 'standard', defaultTasks = ['dewarping','deshadowing','appearance','deblurring'];
let defaultProcessing = {crop:true,autoOrientation:true,shadows:true,deblur:true,appearance:true};
let preparing = false, sending = false, capturing = false, editingBusy = false, connected = false, reordering = false;
let pageDrag = null, pageSwipe = null;
let stream = null, renderAbort = null, batchAbort = null, worker = null, cameraSequence = 0;
let boundaryTimer = null, boundaryCanvas = null, boundaryContext = null, boundaryWorker = null, boundaryBusy = false;
function message(value, error = false) {
    $('status').textContent = t(value); $('status').classList.toggle('error', error);
}
function connection(value) {
    connected = value; updateActions();
}
async function api(path, options = {}) {
    let response;
    try { response = await fetch(path, {...options, headers: {...options.headers, 'X-Capture-Token': token}}); }
    catch (error) {
        if (error.name === 'AbortError') throw error;
        connection(false); throw new Error(t('Could not connect. Keep your computer awake and check Wi-Fi.'));
    }
    if (!response.ok) {
        if (response.status === 401 || response.status === 410) { connection(false); throw new Error(t('Connection expired. Scan the QR code on your computer again.')); }
        if (response.status === 413) throw new Error(t('The file exceeds 64 MB. Choose a smaller file.'));
        if (response.status === 422) throw new Error(t('DocRes could not process this page. Select Standard restoration and retry. Check CUDA memory and the diagnostic log on your computer.'));
        throw new Error(t('Upload or processing failed. Check the file size and format, then retry.'));
    }
    return response;
}
function failure(error) { message(error.name === 'AbortError' ? 'Processing canceled. Your original photo and corner edits are preserved.' : error.message, true); }
function show(next) {
    view = next; document.body.dataset.view = next;
    ['capture','tray','detail'].forEach(id => $(id).hidden = id !== next);
    if (next === 'capture' && stream) startBoundaryDetection(); else stopBoundaryDetection();
    if (next === 'tray') updateReview();
    window.scrollTo({top:0, behavior:'instant'});
}
function updateActions() {
    const pending = items.filter(item => !item.sent), selected = pending.filter(item => item.selected);
    const ready = selected.length > 0 && selected.every(item => item.state === 'ready');
    $('tray-count').textContent = pending.length;
    $('shortcut-label').textContent = t('Scans');
    $('shutter').disabled = !stream || capturing || items.length >= 12;
    $('start-camera').disabled = capturing;
    $('hd-camera').disabled = capturing;
    $('send-batch').disabled = !connected || !ready || preparing || sending || reordering || editingBusy;
    $('select-all').disabled = !pending.length || sending;
    $('select-all').textContent = t(pending.length && selected.length === pending.length ? 'Deselect all' : 'Select all');
    $('send-label').textContent = sending ? t('Sending…') : selected.length ? format('Send ({0})', selected.length) : t('Send');
    $('selection-summary').textContent = selected.length ? format('{0} selected · {1} ready', selected.length, selected.filter(item => item.state === 'ready').length) : '';
    $('empty-tray').hidden = pending.length > 0; $('batch-state').hidden = !preparing;
    $('resume-previews').hidden = preparing || !pending.some(item => item.state === 'local');
    $('capture-tab').disabled = editingBusy || sending || reordering; $('tray-tab').disabled = editingBusy || sending || reordering;
    $('capture-options').querySelectorAll('input,select,#reset-capture-processing').forEach(input => input.disabled = preparing || sending || reordering || editingBusy);
}
function icon(name) {
    const svg = document.createElementNS('http://www.w3.org/2000/svg','svg'); svg.setAttribute('class','icon'); svg.setAttribute('aria-hidden','true');
    const use = document.createElementNS(svg.namespaceURI,'use'); use.setAttribute('href','#i-'+name); svg.append(use); return svg;
}
function stateText(item) { return t(({local:'Waiting for preview',uploading:'Uploading original…',processing:'Generating preview…',ready:'Ready to send',error:'Needs attention'})[item.state]); }
function renderItems() {
    cancelPageDrag();
    $('filmstrip').replaceChildren(); $('pages').replaceChildren(); $('sent-list').replaceChildren();
    items.forEach((item,index) => {
        if (item.sent) {
            const row = document.createElement('li'), name = document.createElement('span'), remove = document.createElement('button');
            name.textContent = item.name; remove.className = 'icon-button quiet'; remove.append(icon('trash'));
            remove.setAttribute('aria-label',format('Remove page {0}',index+1)); remove.disabled = preparing || sending || reordering;
            remove.onclick = () => removeItem(item).catch(failure); row.append(name,remove); $('sent-list').append(row); return;
        }
        const thumb = document.createElement('button'); thumb.setAttribute('aria-label',format('Review page {0}',index+1));
        if (item.resultUrl || item.originalUrl) { const image = document.createElement('img'); image.src = item.resultUrl || item.originalUrl; image.alt = ''; thumb.append(image); }
        else thumb.append(icon('stack'));
        thumb.onclick = openTray; $('filmstrip').append(thumb);
        const row = document.createElement('li'); row.className = 'page'; row.dataset.pageIndex = String(index);
        const plate = document.createElement('div'); plate.className = 'page-image';
        const preview = document.createElement('button'); preview.className = 'page-preview'; preview.setAttribute('aria-label',format('Review page {0}',index+1)); preview.disabled = preparing || sending || reordering;
        if (item.resultUrl || item.originalUrl) { const image = document.createElement('img'); image.src = item.resultUrl || item.originalUrl; image.alt = item.name; image.draggable = false; preview.append(image); }
        else preview.append(icon('stack'));
        preview.onclick = event => { if (preview.dataset.dragged) { event?.preventDefault(); return; } openDetail(item).catch(failure); };
        bindPageReorder(preview,row,item);
        const number = document.createElement('span'); number.className = 'page-index'; number.textContent = item.pdf ? 'PDF' : index+1;
        const label = document.createElement('label'); label.className = 'page-check';
        const select = document.createElement('input'); select.type = 'checkbox'; select.checked = item.selected; select.disabled = sending;
        select.setAttribute('aria-label',format('Select page {0}',index+1)); select.onchange = () => { item.selected = select.checked; updateActions(); };
        label.append(select,icon('check')); plate.append(preview,number,label);
        const meta = document.createElement('div'); meta.className = 'page-meta';
        const text = document.createElement('div'), name = document.createElement('div'), state = document.createElement('span'); text.className = 'page-label';
        name.className = 'page-name'; name.textContent = item.name; state.className = 'page-state'+(item.error ? ' error' : ''); state.textContent = stateText(item); text.append(name,state);
        const remove = document.createElement('button'); remove.className = 'icon-button quiet'; remove.append(icon('trash')); remove.setAttribute('aria-label',format('Remove page {0}',index+1)); remove.disabled = preparing || sending || reordering;
        remove.onclick = () => removeItem(item).catch(failure); meta.append(text,remove); row.append(plate,meta);
        if (item.error) {
            const error = document.createElement('p'); error.className = 'page-error'; error.textContent = item.error;
            const retry = document.createElement('button'); retry.className = 'retry'; retry.textContent = t('Retry this file'); retry.disabled = preparing || sending || reordering;
            retry.onclick = () => { item.state = 'local'; item.error = ''; prepareAll(); }; row.append(error,retry);
        }
        $('pages').append(row);
    });
    const sent = items.filter(item => item.sent).length; $('sent').hidden = !sent; $('sent-count').textContent = format('On computer ({0})',sent); updateActions(); updateReview();
}
function addFiles(files) {
    if (items.length+files.length > 12) { message('Choose up to 12 files per session. Import this batch before starting another.',true); return; }
    if (files.some(file => file.size > 64*1024*1024)) { message('The file exceeds 64 MB. Choose a smaller file.',true); return; }
    if (items.reduce((sum,item) => sum+(item.file?.size || 0),0)+files.reduce((sum,file) => sum+file.size,0) > 256*1024*1024) { message('The batch exceeds 256 MB. Remove some pages and try again.',true); return; }
    files.forEach(file => {
        const pdf = file.type === 'application/pdf' || /\.pdf$/i.test(file.name);
        items.push({file,name:file.name,pdf,originalUrl:pdf ? null : URL.createObjectURL(file),selected:true,state:'local',sent:false});
    });
    renderItems(); message('');
    if (view === 'tray') prepareAll();
}
function choose(input) { const files = [...input.files]; input.value = ''; if (files.length) addFiles(files); }
async function normalized(file) {
    if (['image/jpeg','image/png','image/webp'].includes(file.type) || /\.(jpe?g|png|webp)$/i.test(file.name)) return file;
    const url = URL.createObjectURL(file);
    try {
        const image = new Image(); image.src = url; await image.decode();
        const canvas = document.createElement('canvas'); canvas.width = image.naturalWidth; canvas.height = image.naturalHeight;
        if (canvas.width*canvas.height > 40000000) throw new Error(t('The image exceeds 40 million pixels. Choose a smaller photo.'));
        canvas.getContext('2d').drawImage(image,0,0);
        const blob = await new Promise(resolve => canvas.toBlob(resolve,'image/jpeg',.96));
        if (!blob) throw new Error(t('This image format is not supported. Choose JPEG or PNG.'));
        return new File([blob],file.name.replace(/\.[^.]+$/,'')+'.jpg',{type:'image/jpeg'});
    } finally { URL.revokeObjectURL(url); }
}
async function prepareItem(item,signal) {
    item.error = '';
    if (!item.info) {
        item.state = 'uploading'; renderItems();
        const file = item.pdf ? item.file : await normalized(item.file);
        item.info = await (await api('/upload?name='+encodeURIComponent(file.name),{method:'POST',body:file,signal})).json();
    }
    if (!item.pdf) {
        item.state = 'processing'; renderItems();
        item.correction ||= {corners:[],rotation:0,enhancement:defaultEnhancement,backend:defaultBackend,tasks:[...defaultTasks],processing:{...defaultProcessing}};
        const response = await api('/render/'+item.info.id,{method:'POST',signal,headers:{'Content-Type':'application/json'},body:JSON.stringify(item.correction)});
        const url = URL.createObjectURL(await response.blob()); if (item.resultUrl) URL.revokeObjectURL(item.resultUrl); item.resultUrl = url;
    }
    item.state = 'ready'; renderItems();
}
function prepareAll() {
    if (worker || sending || reordering || editingBusy) return worker;
    const pending = items.filter(item => !item.sent && item.state === 'local');
    if (!pending.length) return Promise.resolve();
    preparing = true; batchAbort = new AbortController(); renderItems(); const signal = batchAbort.signal;
    worker = (async () => {
        let completed = 0; $('progress').max = pending.length; $('progress').value = 0;
        for (const item of pending) {
            if (signal.aborted) break;
            $('batch-progress').textContent = format('Preparing preview {0} of {1}',completed+1,pending.length);
            try { await prepareItem(item,signal); }
            catch (error) { item.state = error.name === 'AbortError' ? 'local' : 'error'; item.error = error.name === 'AbortError' ? '' : error.message; if (error.name === 'AbortError') break; }
            $('progress').value = ++completed;
        }
    })().finally(() => {
        worker = null; preparing = false; batchAbort = null; renderItems();
        if (signal.aborted) message('Processing canceled. Your original photo and corner edits are preserved.');
        else if (view === 'tray') message(items.some(item => item.state === 'error') ? 'Some pages need attention. Retry or deselect them.' : '');
    });
    return worker;
}
function openTray() { if (editingBusy || sending || reordering) return; resetPhotoZoom(); show('tray'); renderItems(); prepareAll(); }
async function removeItem(item) {
    if (preparing || sending || reordering || editingBusy) return;
    if (item.info) await api('/remove/'+item.info.id,{method:'POST'});
    items.splice(items.indexOf(item),1); [item.originalUrl,item.resultUrl,item.editorUrl].filter(Boolean).forEach(url => URL.revokeObjectURL(url));
    if (active === item) { active = null; show('tray'); } renderItems(); message('Photo discarded.');
}
async function refresh() {
    const data = await (await api('/status')).json(); connection(true);
    const neuralReady = !!(data.restoration?.eligible && data.restoration.installed && data.restoration.enabled);
    $('docres-option').disabled = !neuralReady; $('capture-docres-option').disabled = !neuralReady; $('docres-availability').hidden = neuralReady;
    for (const info of data.files) {
        let item = items.find(value => value.info?.id === info.id);
        if (!item) {
            item = {info,name:info.name,pdf:info.pdf,correction:info.correction,selected:!info.ready,sent:info.ready,state:info.pdf || info.processed ? 'ready' : 'local'}; items.push(item);
            if (info.processed && !info.ready) item.resultUrl = URL.createObjectURL(await (await api('/result/'+info.id)).blob());
        } else if (info.ready) item.sent = true;
    }
    renderItems();
}
function renderPaperGuide(points,sourceWidth,sourceHeight) {
    const video = $('live');
    if (!stream || view !== 'capture' || sourceWidth !== video.videoWidth || sourceHeight !== video.videoHeight) return;
    const rect = video.getBoundingClientRect();
    $('framing-guides').toggleAttribute('hidden',!points || !rect.width || !rect.height);
    if (!points || !rect.width || !rect.height) return;
    const mapped = mapPaperCorners(points,sourceWidth,sourceHeight,rect.width,rect.height);
    $('live-boundary').setAttribute('points',mapped.map(point => point.map(value => value*100).join(',')).join(' '));
}
function updateFramingGuides() {
    const video = $('live');
    if (!stream || view !== 'capture' || document.hidden || capturing || boundaryBusy || video.readyState < 2 || !video.videoWidth || !video.videoHeight) return;
    try {
        const scale = Math.min(1,320/Math.max(video.videoWidth,video.videoHeight));
        const width = Math.max(3,Math.round(video.videoWidth*scale)), height = Math.max(3,Math.round(video.videoHeight*scale));
        if (boundaryCanvas.width !== width) boundaryCanvas.width = width;
        if (boundaryCanvas.height !== height) boundaryCanvas.height = height;
        boundaryContext.drawImage(video,0,0,boundaryCanvas.width,boundaryCanvas.height);
        const pixels = boundaryContext.getImageData(0,0,width,height).data;
        if (boundaryWorker) {
            boundaryBusy = true;
            boundaryWorker.postMessage({pixels,width,height,sourceWidth:video.videoWidth,sourceHeight:video.videoHeight},[pixels.buffer]);
        } else renderPaperGuide(detectPaperCorners(pixels,width,height),video.videoWidth,video.videoHeight);
    } catch (error) {
        console.error('Live paper boundary detection failed.',error); stopBoundaryDetection();
    }
}
function startBoundaryDetection() {
    if (boundaryTimer !== null) return;
    boundaryCanvas = document.createElement('canvas'); boundaryContext = boundaryCanvas.getContext('2d',{willReadFrequently:true});
    if (typeof Worker === 'function') {
        try {
            boundaryWorker = new Worker('/paper-worker.js',{type:'module'});
            boundaryWorker.onmessage = event => { boundaryBusy = false; renderPaperGuide(event.data.points,event.data.sourceWidth,event.data.sourceHeight); };
            boundaryWorker.onerror = error => { console.error('Paper boundary worker failed.',error); stopBoundaryDetection(); };
        } catch (error) { console.error('Paper boundary worker could not start. Using slower preview analysis.',error); }
    }
    boundaryTimer = setInterval(updateFramingGuides,boundaryWorker ? 200 : 400);
}
function stopBoundaryDetection() {
    if (boundaryTimer !== null) clearInterval(boundaryTimer);
    boundaryWorker?.terminate(); boundaryWorker = null; boundaryBusy = false;
    boundaryTimer = null; boundaryCanvas = null; boundaryContext = null; $('framing-guides').toggleAttribute('hidden',true);
}
async function startCamera() {
    if (!navigator.mediaDevices?.getUserMedia || !window.isSecureContext) { message('Continuous capture requires trusted HTTPS. Use system camera or choose files on this connection.',true); return; }
    const sequence = ++cameraSequence; capturing = true; updateActions();
    try {
        const camera = await navigator.mediaDevices.getUserMedia({audio:false,video:{facingMode:{ideal:'environment'},width:{ideal:1920},height:{ideal:1440},frameRate:{ideal:30}}});
        if (sequence !== cameraSequence || document.hidden) { camera.getTracks().forEach(track => track.stop()); return; }
        stopCamera(false); stream = camera; $('live').srcObject = camera; await $('live').play();
        $('camera-empty').hidden = true; message('');
        startBoundaryDetection();
        camera.getVideoTracks()[0].onended = () => stopCamera();
    } catch (error) {
        stopCamera(); message(error.name === 'NotAllowedError' ? 'Camera access was denied. Allow camera access in your browser, or use system camera.' : 'Camera unavailable. Close other camera apps and retry, or use system camera.',true);
    } finally { capturing = false; updateActions(); }
}
function stopCamera(invalidate = true) {
    if (invalidate) cameraSequence++;
    stopBoundaryDetection();
    if (stream) stream.getTracks().forEach(track => { track.onended = null; track.stop(); });
    stream = null; $('live').srcObject = null; $('camera-empty').hidden = false; updateActions();
}
async function takePhoto() {
    if (!stream || capturing || items.length >= 12) return;
    capturing = true; updateActions();
    try {
        let blob = null, highResolutionFailed = false;
        if (highResolution) {
            try {
                const camera = new ImageCapture(stream.getVideoTracks()[0]);
                const capabilities = await camera.getPhotoCapabilities();
                blob = await camera.takePhoto({imageWidth:capabilities.imageWidth.max});
            } catch (error) {
                console.error('High-resolution photo capture failed. Falling back to the preview frame.',error);
                highResolution = false; highResolutionFailed = true; $('hd-camera').setAttribute('aria-pressed','false');
            }
        }
        if (!blob) {
            const video = $('live'), canvas = document.createElement('canvas'); canvas.width = video.videoWidth; canvas.height = video.videoHeight;
            if (!canvas.width || !canvas.height) throw new Error(t('Camera is starting. Try again in a moment.'));
            canvas.getContext('2d').drawImage(video,0,0); blob = await new Promise(resolve => canvas.toBlob(resolve,'image/jpeg',.96));
        }
        if (!blob) throw new Error(t('Camera is starting. Try again in a moment.'));
        const extension = blob.type === 'image/png' ? '.png' : blob.type === 'image/webp' ? '.webp' : '.jpg';
        addFiles([new File([blob],'Scan-'+String(items.length+1).padStart(2,'0')+extension,{type:blob.type || 'image/jpeg'})]);
        $('flash').classList.remove('flash'); void $('flash').offsetWidth; $('flash').classList.add('flash');
        if (!multiCapture) openTray();
        if (highResolutionFailed) message('High-resolution capture failed. A standard photo was saved.',true);
    } catch (error) { failure(error); } finally { capturing = false; updateActions(); }
}
function draw() {
    $('polygon').setAttribute('points',corners.map(point => [point[0]*100,point[1]*100].join(',')).join(' '));
    controls.forEach((button,index) => { button.style.left = corners[index][0]*100+'%'; button.style.top = corners[index][1]*100+'%'; });
}
for (let index = 0; index < 4; index++) {
    const button = document.createElement('button'); button.className = 'corner'; button.setAttribute('aria-label',t(['Top left corner','Top right corner','Bottom right corner','Bottom left corner'][index]));
    let dragging = false;
    const move = event => { const rect = $('editor').getBoundingClientRect(); corners[index] = [Math.max(0,Math.min(1,(event.clientX-rect.left)/rect.width)),Math.max(0,Math.min(1,(event.clientY-rect.top)/rect.height))]; enableManualCrop(); draw(); };
    button.onpointerdown = event => { if (editingBusy) return; dragging = true; button.setPointerCapture(event.pointerId); move(event); };
    button.onpointermove = event => { if (dragging) move(event); }; button.onpointerup = button.onpointercancel = () => dragging = false;
    button.onkeydown = event => {
        if (editingBusy || !event.key.startsWith('Arrow')) return; event.preventDefault(); const delta = event.shiftKey ? .025 : .005;
        if (event.key === 'ArrowLeft') corners[index][0] -= delta; if (event.key === 'ArrowRight') corners[index][0] += delta;
        if (event.key === 'ArrowUp') corners[index][1] -= delta; if (event.key === 'ArrowDown') corners[index][1] += delta;
        corners[index] = corners[index].map(value => Math.max(0,Math.min(1,value))); enableManualCrop(); draw();
    };
    controls.push(button); $('editor').append(button);
}
function detailBusy(value) {
    editingBusy = value; $('detail').querySelectorAll('button,input,select').forEach(element => element.disabled = element.id === 'cancel-render' ? false : value);
    $('docres-option').disabled = !$('docres-availability').hidden; updateProcessingControls(); updateActions();
}
async function openDetail(item) {
    if (preparing || sending || reordering) return;
    reviewIndex = Math.max(0,pendingPages().indexOf(item)); reviewLayout = 'image'; filterOpen = false; showOriginal = false;
    active = null; editing = false; show('tray'); renderItems(); message('');
}
async function editPage() {
    detailBusy(true);
    try {
        const blob = await (await api('/image/'+active.info.id)).blob(); if (active.editorUrl) URL.revokeObjectURL(active.editorUrl);
        active.editorUrl = URL.createObjectURL(blob); active.originalUrl ||= URL.createObjectURL(blob); $('photo').src = active.editorUrl; $('photo').alt = t('Photo preview');
        corners = active.info.corners.map(point => [...point]); rotation = active.info.rotation;
        automaticCrop = !active.correction?.corners?.length;
        if (active.correction) {
            const turns = (active.correction.rotation-rotation+360)%360/90;
            for (let turn = 0; turn < turns; turn++) await rotatePhoto();
            if (!automaticCrop) corners = active.correction.corners.map(point => [...point]);
        }
        $('backend').value = active.correction?.backend || 'standard';
        $('standard-processing').querySelectorAll('[data-process]').forEach(input => input.checked = !!active.correction?.processing?.[input.dataset.process]);
        if ($('docres-option').disabled) $('backend').value = 'standard';
        const tasks = active.correction?.tasks || []; $('docres-tasks').querySelectorAll('input').forEach(input => input.checked = tasks.includes(input.value));
        $('docres-tasks').hidden = $('backend').value !== 'docres';
        editing = true; $('review').hidden = false; $('result').hidden = true; $('save-page').textContent = t('Save preview'); draw();
    } catch (error) { failure(error); } finally { detailBusy(false); }
}
async function rotatePhoto() {
    const image = $('photo'); await image.decode(); const canvas = document.createElement('canvas'); canvas.width = image.naturalHeight; canvas.height = image.naturalWidth;
    const context = canvas.getContext('2d'); context.translate(canvas.width/2,canvas.height/2); context.rotate(Math.PI/2); context.drawImage(image,-image.naturalWidth/2,-image.naturalHeight/2);
    const blob = await new Promise(resolve => canvas.toBlob(resolve,'image/png'));
    if (!blob) throw new Error(t('Upload or processing failed. Check the file size and format, then retry.'));
    const old = active.editorUrl; active.editorUrl = URL.createObjectURL(blob); image.src = active.editorUrl; await image.decode(); if (old) URL.revokeObjectURL(old);
    corners = [corners[3],corners[0],corners[1],corners[2]].map(point => [1-point[1],point[0]]); rotation = (rotation+90)%360; draw();
}
async function savePage() {
    if (!editing) { active = null; openTray(); return; }
    const crosses = corners.map((point,index) => { const q = corners[(index+1)%4], r = corners[(index+2)%4]; return (q[0]-point[0])*(r[1]-point[1])-(q[1]-point[1])*(r[0]-point[0]); });
    const area = Math.abs(corners.reduce((sum,point,index) => { const q = corners[(index+1)%4]; return sum+point[0]*q[1]-q[0]*point[1]; },0))/2;
    const processing = readProcessing($('standard-processing'));
    if ($('backend').value === 'standard' && processing.crop && !automaticCrop && (crosses.some(value => value < .001) || area < .01)) { message('Corners overlap or are too close. Adjust the four handles.',true); return; }
    const item = active; item.correction = {corners:automaticCrop ? [] : corners.map(point => [...point]),rotation,enhancement:'original',backend:$('backend').value,tasks:[...$('docres-tasks').querySelectorAll('input:checked')].map(input => input.value),processing};
    detailBusy(true); message('Applying selected processing…'); renderAbort = new AbortController(); $('cancel-render').hidden = false;
    const stageTimer = setInterval(async () => { try { const stage = await (await api('/progress')).text(); if (stage && renderAbort && !renderAbort.signal.aborted) message(stage); } catch {} },1000);
    try { await prepareItem(item,renderAbort.signal); await openDetail(item); }
    catch (error) { item.state = 'error'; item.error = t(error.name === 'AbortError' ? 'Processing canceled. Your original photo and corner edits are preserved.' : error.message); failure(error); }
    finally { clearInterval(stageTimer); renderAbort = null; $('cancel-render').hidden = true; detailBusy(false); }
}
$('capture-tab').onclick = $('empty-camera').onclick = () => { active = null; show('capture'); message(''); };
$('tray-tab').onclick = $('tray-shortcut').onclick = openTray;
$('start-camera').onclick = startCamera; $('shutter').onclick = takePhoto;
$('camera').onchange = event => { const picked = event.target.files.length > 0; choose(event.target); if (picked && !multiCapture) openTray(); };
$('files').onchange = event => choose(event.target);
$('select-all').onclick = () => { const pending = items.filter(item => !item.sent), select = !pending.every(item => item.selected); pending.forEach(item => item.selected = select); renderItems(); };
$('stop-processing').onclick = () => batchAbort?.abort(); $('cancel-render').onclick = () => renderAbort?.abort();
$('resume-previews').onclick = prepareAll;
$('backend').onchange = updateProcessingControls;
$('whole').onclick = () => { automaticCrop = false; $('standard-processing').querySelector('[data-process="crop"]').checked = false; corners = [[0,0],[1,0],[1,1],[0,1]]; draw(); };
$('rotate').onclick = async () => { detailBusy(true); try { await rotatePhoto(); } catch (error) { failure(error); } finally { detailBusy(false); } };
$('save-page').onclick = savePage;
$('discard').onclick = () => removeItem(active).catch(failure); $('detail-back').onclick = () => { active = null; openTray(); };
$('send-batch').onclick = async () => {
    const selected = items.filter(item => !item.sent && item.selected);
    if (!selected.length || selected.some(item => item.state !== 'ready') || preparing || sending || reordering) return;
    sending = true; renderItems();
    try {
        await syncPageOrder();
        await api('/accept-batch',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({ids:selected.map(item => item.info.id)})});
        selected.forEach(item => { item.sent = true; item.selected = false; }); message('Sent. Confirm the import on your computer.'); $('sent').open = true;
    } catch (error) { failure(error); } finally { sending = false; renderItems(); }
};


function readProcessing(fieldset) {
    const options = {};
    fieldset.querySelectorAll('[data-process]').forEach(input => options[input.dataset.process] = input.checked);
    return options;
}
function enableManualCrop() {
    automaticCrop = false;
    const input = $('standard-processing').querySelector('[data-process="crop"]');
    if (input) input.checked = true;
}
function updateProcessingControls() {
    const docres = $('backend').value === 'docres';
    $('standard-processing').hidden = docres;
    $('docres-tasks').hidden = !docres;
    $('docres-direct').hidden = !docres;
    $('outline').hidden = docres;
    controls.forEach(button => button.hidden = docres);
    $('rotate').disabled = docres || editingBusy;
    $('whole').disabled = docres || editingBusy;
    if (active) $('photo').src = docres ? active.originalUrl || active.editorUrl : active.editorUrl;
}
function captureOptionsChanged() {
    if (preparing || sending || reordering || editingBusy) return Promise.resolve();
    defaultBackend = $('capture-backend').value || 'standard';
    defaultProcessing = readProcessing($('capture-processing'));
    defaultTasks = [...$('capture-docres-tasks').querySelectorAll('input:checked')].map(input => input.value);
    $('capture-processing').hidden = defaultBackend === 'docres';
    $('capture-docres-tasks').hidden = defaultBackend !== 'docres';
    items.filter(item => !item.sent && !item.pdf).forEach(item => {
        item.correction = {...item.correction,corners:item.correction?.corners || [],rotation:item.correction?.rotation || 0,enhancement:'original',backend:defaultBackend,tasks:[...defaultTasks],processing:{...defaultProcessing}};
        item.state = 'local'; item.error = '';
    });
    renderItems();
    return view === 'tray' && !$('capture-options').open ? prepareAll() : Promise.resolve();
}
$('capture-backend').onchange = captureOptionsChanged;
$('capture-processing').onchange = captureOptionsChanged;
$('capture-docres-tasks').onchange = captureOptionsChanged;
$('reset-capture-processing').onclick = () => {
    $('capture-processing').querySelectorAll('input').forEach(input => input.checked = false);
    $('capture-docres-tasks').querySelectorAll('input').forEach(input => input.checked = false);
    items.filter(item => !item.sent && !item.pdf && item.correction).forEach(item => item.correction.rotation = 0);
    return captureOptionsChanged();
};
$('reset-page-processing').onclick = async () => {
    if (!active) return;
    active.correction = {corners:[],rotation:0,backend:$('backend').value,enhancement:'original',tasks:[],processing:{}};
    await editPage();
};

function pendingPages() { return items.filter(item => !item.sent); }
function currentPage() {
    const pending = pendingPages();
    reviewIndex = Math.max(0,Math.min(reviewIndex,pending.length-1));
    return pending[reviewIndex] || null;
}
function updateReview() {
    const pending = pendingPages(), item = currentPage(), grid = reviewLayout === 'grid';
    $('review-stage').hidden = !item || grid;
    $('pages').hidden = !grid || !pending.length;
    $('grid-toolbar').hidden = !grid || !pending.length;
    $('review-dock').hidden = !pending.length;
    $('image-view').setAttribute('aria-pressed',String(!grid));
    $('tray-tab').setAttribute('aria-pressed',String(grid));
    $('preview-tab').setAttribute('aria-pressed',String(!grid && !filterOpen));
    $('filter-tab').setAttribute('aria-pressed',String(filterOpen));
    $('pages-tab').setAttribute('aria-pressed',String(grid));
    $('filter-presets').hidden = !filterOpen || grid;
    $('grid-summary').textContent = format('{0} pages in review',pending.length);
    $('page-position').textContent = pending.length ? (reviewIndex+1)+'/'+pending.length : '0/0';
    $('previous-page').disabled = reviewIndex === 0;
    $('next-page').disabled = reviewIndex >= pending.length-1;
    $('tray-count').hidden = !pending.length;
    $('current-selected').checked = !!item?.selected;
    $('current-selected').disabled = sending;
    const editable = !!item && !item.pdf && item.state === 'ready' && !preparing && !sending && !reordering && !editingBusy;
    ['filter-tool','restoration-tool','edit-page','compare-source'].forEach(id => $(id).disabled = !editable);
    ['restoration-tool','edit-page'].forEach(id => $(id).disabled = !item?.info || item.pdf || preparing || sending || reordering || editingBusy);
    $('filter-tool').disabled = !editable || item?.correction?.backend === 'docres';
    $('filter-tab').disabled = !editable || item?.correction?.backend === 'docres';
    $('delete-page').disabled = !item || preparing || sending || reordering;
    $('compare-source').setAttribute('aria-pressed',String(showOriginal));
    if (!item) return;
    $('pdf-preview').hidden = !item.pdf;
    const url = showOriginal ? item.originalUrl || item.editorUrl : item.resultUrl || item.originalUrl;
    const image = $('tray-result');
    image.hidden = item.pdf || !url;
    if (url && image.src !== url) { image.src = url; resetPhotoZoom(); }
    image.alt = t(showOriginal ? 'Photo preview' : 'Corrected page preview');
    document.querySelectorAll('[data-filter]').forEach(button => {
        button.disabled = !editable || item?.correction?.backend === 'docres';
        button.setAttribute('aria-pressed',String(button.dataset.filter === (item.correction?.enhancement || defaultEnhancement)));
    });
}
function changeReviewPage(delta,animate = false) {
    if (reordering || pageDrag) return;
    const next = Math.max(0,Math.min(pendingPages().length-1,reviewIndex+delta));
    const stage = $('review-stage');
    stage.classList.remove('swiping'); stage.style.transform = '';
    if (next === reviewIndex) return;
    reviewIndex = next; showOriginal = false; resetPhotoZoom(); updateReview();
    if (animate && !window.matchMedia?.('(prefers-reduced-motion: reduce)').matches) {
        stage.classList.add('swiping'); stage.style.transform = 'translateX('+(delta > 0 ? 24 : -24)+'px)';
        requestAnimationFrame(() => requestAnimationFrame(() => { stage.classList.remove('swiping'); stage.style.transform = ''; }));
    }
}
const photoView = {scale:1,x:0,y:0};
const stagePointers = new Map();
let stageGesture = null;
function photoBox() {
    const stage = $('review-stage'), image = $('tray-result');
    const width = stage.clientWidth, height = stage.clientHeight;
    const naturalWidth = image.naturalWidth || width, naturalHeight = image.naturalHeight || height;
    if (!width || !height || !naturalWidth || !naturalHeight) return null;
    const ratio = naturalWidth/naturalHeight;
    let imageWidth = width, imageHeight = width/ratio;
    if (imageHeight > height) { imageHeight = height; imageWidth = height*ratio; }
    return {width,height,imageWidth,imageHeight,left:(width-imageWidth)/2,top:0};
}
function clampPhotoView(box = photoBox()) {
    if (!box) return;
    const scaledWidth = box.imageWidth*photoView.scale, scaledHeight = box.imageHeight*photoView.scale;
    const left = box.left*photoView.scale, top = box.top*photoView.scale;
    if (scaledWidth <= box.width) photoView.x = (box.width-scaledWidth)/2-left;
    else photoView.x = Math.max(box.width-scaledWidth-left,Math.min(-left,photoView.x));
    if (scaledHeight <= box.height) photoView.y = -top;
    else photoView.y = Math.max(box.height-scaledHeight-top,Math.min(-top,photoView.y));
}
function applyPhotoView() {
    $('tray-result').style.transform = photoView.scale === 1 && !photoView.x && !photoView.y ? '' : 'translate('+photoView.x+'px,'+photoView.y+'px) scale('+photoView.scale+')';
}
function resetPhotoZoom() {
    stagePointers.clear(); stageGesture = null;
    photoView.scale = 1; photoView.x = 0; photoView.y = 0; applyPhotoView();
}
function pinchMetrics() {
    const points = [...stagePointers.values()];
    return {x:(points[0].x+points[1].x)/2,y:(points[0].y+points[1].y)/2,distance:Math.hypot(points[0].x-points[1].x,points[0].y-points[1].y) || 1};
}
function beginPinch() {
    resetPageSwipe();
    const view = pinchMetrics();
    stageGesture = {mode:'pinch',distance:view.distance,midX:view.x,midY:view.y,scale:photoView.scale,x:photoView.x,y:photoView.y};
}
function resetPageSwipe() {
    pageSwipe = null; $('review-stage').classList.remove('swiping'); $('review-stage').style.transform = '';
}
$('review-stage').onpointerdown = event => {
    if (reviewLayout !== 'image' || reordering || event.target.closest('button,input,label')) return;
    if (event.pointerType === 'mouse' && event.button !== 0) return;
    if (stagePointers.size >= 2 || stagePointers.has(event.pointerId)) return;
    stagePointers.set(event.pointerId,{x:event.clientX,y:event.clientY});
    $('review-stage').setPointerCapture(event.pointerId);
    if (stagePointers.size === 2) { beginPinch(); return; }
    pageSwipe = {id:event.pointerId,x:event.clientX,y:event.clientY,dx:0,horizontal:false};
    stageGesture = {mode:photoView.scale > 1 ? 'pan' : null,x:event.clientX,y:event.clientY,tx:photoView.x,ty:photoView.y};
};
$('review-stage').onpointermove = event => {
    const point = stagePointers.get(event.pointerId);
    if (!point) return;
    point.x = event.clientX; point.y = event.clientY;
    if (stagePointers.size >= 2) {
        if (stageGesture?.mode !== 'pinch') return;
        const view = pinchMetrics(), gesture = stageGesture;
        const scale = Math.max(1,Math.min(5,gesture.scale*view.distance/gesture.distance));
        const factor = scale/gesture.scale;
        photoView.scale = scale;
        photoView.x = view.x-(gesture.midX-gesture.x)*factor;
        photoView.y = view.y-(gesture.midY-gesture.y)*factor;
        clampPhotoView(); applyPhotoView(); return;
    }
    const gesture = stageGesture;
    if (!gesture) return;
    const dx = event.clientX-gesture.x, dy = event.clientY-gesture.y;
    if (gesture.mode === 'pan') {
        photoView.x = gesture.tx+dx; photoView.y = gesture.ty+dy; clampPhotoView(); applyPhotoView(); return;
    }
    if (!gesture.mode) {
        if (Math.abs(dy) > Math.max(12,Math.abs(dx))) { gesture.mode = 'vertical'; return; }
        if (!(Math.abs(dx) > 12 && Math.abs(dx) > Math.abs(dy)*1.3)) return;
        gesture.mode = 'swipe'; $('review-stage').classList.add('swiping');
    }
    if (gesture.mode !== 'swipe' || !pageSwipe) return;
    pageSwipe.dx = dx;
    const edge = dx > 0 ? reviewIndex === 0 : reviewIndex === pendingPages().length-1;
    $('review-stage').style.transform = 'translateX('+(dx*(edge ? .2 : 1))+'px)';
};
function endStagePointer(event, canceled) {
    if (!stagePointers.delete(event.pointerId)) return;
    const gesture = stageGesture;
    if (stagePointers.size) {
        if (gesture?.mode === 'pinch') {
            const point = [...stagePointers.values()][0];
            stageGesture = {mode:photoView.scale > 1 ? 'pan' : null,x:point.x,y:point.y,tx:photoView.x,ty:photoView.y};
        }
        return;
    }
    stageGesture = null;
    const swipe = pageSwipe;
    resetPageSwipe();
    if (!canceled && gesture?.mode === 'swipe' && swipe) {
        const threshold = Math.max(42,$('review-stage').clientWidth*.16);
        if (Math.abs(swipe.dx) >= threshold) changeReviewPage(swipe.dx < 0 ? 1 : -1,true);
    }
    if (photoView.scale <= 1.01) { photoView.scale = 1; photoView.x = 0; photoView.y = 0; clampPhotoView(); applyPhotoView(); }
}
$('review-stage').onpointerup = event => endStagePointer(event,false);
$('review-stage').onpointercancel = event => endStagePointer(event,true);
async function syncPageOrder() {
    const ids = items.filter(item => !item.sent && item.info).map(item => item.info.id);
    if (ids.length) await api('/reorder',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({ids})});
}
async function reorderPages(item,target) {
    if (item === target || item.sent || target.sent || preparing || sending || reordering || editingBusy) return;
    const previous = [...items], current = currentPage();
    const targetIndex = items.indexOf(target);
    items.splice(items.indexOf(item),1); items.splice(targetIndex,0,item);
    reviewIndex = pendingPages().indexOf(current); reordering = true; renderItems();
    try { await syncPageOrder(); message('Page order updated.'); }
    catch (error) { items.splice(0,items.length,...previous); reviewIndex = pendingPages().indexOf(current); failure(error); }
    finally { reordering = false; renderItems(); }
}
function cancelPageDrag() {
    if (!pageDrag) return;
    clearTimeout(pageDrag.timer); clearInterval(pageDrag.scroll);
    pageDrag.row.classList.remove('dragging'); pageDrag.preview.style.transform = '';
    pageDrag.targetRow?.classList.remove('drag-target'); pageDrag = null;
}
function bindPageReorder(preview,row,item) {
    function begin(x,y) {
        if (preparing || sending || reordering || editingBusy) return;
        cancelPageDrag();
        const drag = {preview,row,item,x,y,lastX:x,lastY:y,scrollY:window.scrollY,active:false,target:item};
        pageDrag = drag;
        drag.timer = setTimeout(() => {
            if (pageDrag !== drag) return;
            drag.active = true; preview.dataset.dragged = 'true'; row.classList.add('dragging');
            navigator.vibrate?.(15);
            drag.scroll = setInterval(() => {
                if (drag.lastY < 90) window.scrollBy(0,-12);
                else if (drag.lastY > window.innerHeight-130) window.scrollBy(0,12);
                move(drag.lastX,drag.lastY);
            },32);
        },450);
    }
    function move(x,y,event) {
        const drag = pageDrag;
        if (!drag || drag.preview !== preview) return;
        drag.lastX = x; drag.lastY = y;
        if (!drag.active) { if (Math.hypot(x-drag.x,y-drag.y) > 10) cancelPageDrag(); return; }
        event?.preventDefault();
        preview.style.transform = 'translate('+(x-drag.x)+'px,'+(y-drag.y+window.scrollY-drag.scrollY)+'px)';
        const targetRow = document.elementFromPoint(x,y)?.closest('.page');
        const target = targetRow ? items[Number(targetRow.dataset.pageIndex)] : null;
        drag.targetRow?.classList.remove('drag-target'); drag.targetRow = null; drag.target = item;
        if (target && !target.sent) { drag.targetRow = targetRow; drag.target = target; targetRow.classList.add('drag-target'); }
    }
    function finish(event,canceled = false) {
        const drag = pageDrag;
        if (!drag || drag.preview !== preview) return;
        if (drag.active) { event?.preventDefault(); setTimeout(() => delete preview.dataset.dragged,350); }
        cancelPageDrag();
        if (drag.active && !canceled) reorderPages(item,drag.target).catch(failure);
    }
    preview.addEventListener('touchstart',event => {
        if (event.touches.length !== 1) { cancelPageDrag(); return; }
        begin(event.touches[0].clientX,event.touches[0].clientY);
    },{passive:true});
    preview.addEventListener('touchmove',event => {
        if (event.touches.length !== 1) { finish(event,true); return; }
        move(event.touches[0].clientX,event.touches[0].clientY,event);
    },{passive:false});
    preview.addEventListener('touchend',event => finish(event),{passive:false});
    preview.addEventListener('touchcancel',event => finish(event,true),{passive:true});
    preview.oncontextmenu = event => event.preventDefault();
    preview.onpointerdown = event => {
        if (event.pointerType === 'touch' || event.button !== 0) return;
        begin(event.clientX,event.clientY); preview.setPointerCapture(event.pointerId);
    };
    preview.onpointermove = event => { if (event.pointerType !== 'touch') move(event.clientX,event.clientY,event); };
    preview.onpointerup = event => { if (event.pointerType !== 'touch') finish(event); };
    preview.onpointercancel = () => cancelPageDrag();
}
function setReviewLayout(layout) {
    reviewLayout = layout; filterOpen = false; showOriginal = false;
    resetPhotoZoom();
    if (view !== 'tray') openTray();
    updateReview(); message('');
}
async function beginEdit() {
    const item = currentPage();
    if (!item || item.pdf || !item.info || preparing || sending || reordering) return;
    active = item; editing = false;
    $('detail-title').textContent = format('Page {0}',items.indexOf(item)+1);
    $('result').hidden = true; show('detail'); message('');
    await editPage();
    $('advanced-restoration').open = false;
}
async function applyFilter(mode) {
    const item = currentPage();
    if (!item || item.state !== 'ready' || preparing || sending || reordering) return;
    item.correction ||= {corners:[],rotation:0,backend:'standard',tasks:[...defaultTasks],processing:{...defaultProcessing}};
    if (item.correction.backend === 'docres') return;
    item.correction.processing ||= {};
    if (mode === 'gray') item.correction.processing.grayscale = true;
    else if (mode === 'color') { item.correction.processing.grayscale = false; item.correction.processing.appearance = true; }
    else ['shadows','deblur','appearance','grayscale'].forEach(key => item.correction.processing[key] = false);
    item.correction.enhancement = mode; item.state = 'local'; showOriginal = false;
    await prepareAll();
    updateReview();
}
function continueShooting() { active = null; show('capture'); message(''); }
$('image-view').onclick = $('preview-tab').onclick = () => setReviewLayout('image');
$('tray-tab').onclick = $('pages-tab').onclick = () => setReviewLayout('grid');
$('continue-camera').onclick = continueShooting;
$('previous-page').onclick = () => changeReviewPage(-1,true);
$('next-page').onclick = () => changeReviewPage(1,true);
$('current-selected').onchange = event => { const item = currentPage(); if (item) item.selected = event.target.checked; renderItems(); };
$('edit-page').onclick = () => beginEdit().catch(failure);
$('restoration-tool').onclick = () => $('capture-options').showModal();
$('filter-tool').onclick = $('filter-tab').onclick = () => { reviewLayout = 'image'; filterOpen = !filterOpen; updateReview(); };
document.querySelectorAll('[data-filter]').forEach(button => button.onclick = () => applyFilter(button.dataset.filter).catch(failure));
$('delete-page').onclick = () => { const item = currentPage(); if (item) removeItem(item).catch(failure); };
$('compare-source').onclick = async () => {
    const item = currentPage();
    if (!item || item.pdf) return;
    try {
        if (!item.originalUrl) item.originalUrl = URL.createObjectURL(await (await api('/image/'+item.info.id)).blob());
        showOriginal = !showOriginal; updateReview();
    } catch (error) { failure(error); }
};
$('single-mode').onclick = () => { multiCapture = false; $('single-mode').setAttribute('aria-pressed','true'); $('multi-mode').setAttribute('aria-pressed','false'); };
$('multi-mode').onclick = () => { multiCapture = true; $('single-mode').setAttribute('aria-pressed','false'); $('multi-mode').setAttribute('aria-pressed','true'); };
$('camera-settings').onclick = $('camera-tools').onclick = $('review-options').onclick = () => $('capture-options').showModal();
$('close-options').onclick = () => $('capture-options').close();
$('capture-options').onclose = () => view === 'tray' ? prepareAll() : Promise.resolve();

$('capture-processing').querySelectorAll('[data-process]').forEach(input => input.checked = !!defaultProcessing[input.dataset.process]);
$('capture-docres-tasks').querySelectorAll('input').forEach(input => input.checked = defaultTasks.includes(input.value));

$('pdf-files').onchange = event => choose(event.target);
$('hd-camera').onclick = () => {
    if (!highResolution && typeof ImageCapture !== 'function') {
        message('High-resolution capture is unavailable in this browser. Use system camera for full-resolution photos.',true); return;
    }
    highResolution = !highResolution;
    $('hd-camera').setAttribute('aria-pressed',String(highResolution)); message('');
};

document.addEventListener('visibilitychange',() => { if (document.hidden) stopCamera(); });
window.addEventListener('pagehide',() => stopCamera());
window.addEventListener('beforeunload',event => { if (items.some(item => !item.sent && !item.info)) { event.preventDefault(); event.returnValue = ''; } });
refresh().catch(failure);
setInterval(() => { if (!preparing && !editingBusy && !sending && !reordering && !pageDrag) refresh().catch(failure); },10000);
