import { mkdir, writeFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
// Fixed upstream release. Font bytes stay in ignored developer assets, not system Fonts.
const url = 'https://raw.githubusercontent.com/adobe-fonts/source-han-serif/2.003R/OTF/SimplifiedChinese/SourceHanSerifSC-Regular.otf';
await mkdir(new URL('assets/', import.meta.url), { recursive: true });
const response = await fetch(url);
if (!response.ok) throw new Error(`Font download failed: ${response.status}`);
const bytes = Buffer.from(await response.arrayBuffer());
const expected = '78aa7a328fd974df2d688c8a9fd74a33d8334dfa84ab24d9d11efb2ffc464117';
if (createHash('sha256').update(bytes).digest('hex') !== expected) throw new Error('Font checksum mismatch');
await writeFile(new URL('assets/SourceHanSerifSC-Regular.otf', import.meta.url), bytes);
const license = await fetch('https://raw.githubusercontent.com/adobe-fonts/source-han-serif/2.003R/LICENSE.txt');
if (!license.ok) throw new Error('Font license download failed');
await writeFile(new URL('assets/OFL.txt', import.meta.url), await license.text());
console.log(JSON.stringify({ url, bytes: bytes.length, sha256: createHash('sha256').update(bytes).digest('hex') }));
