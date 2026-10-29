import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createClient, parseRelated, validateId } from './runtime.mjs';
const response = (status, body = {}, headers = {}) => new Response(JSON.stringify(body), { status, headers });
const payload = { resources: { 'shazam-song-lists': { 'track-similarities-id-123': { relationships: { tracks: { data: [{id:'4'}, {id:'2'}, {id:'4'}, {id:'9'}, {id:'123'}] } } } }, 'shazam-songs': { '2': { attributes: { title:'Second', artist:'Two', isrc:'US1234567890' } }, '4': { attributes: { title:'First', artist:'One', url:'https://www.shazam.com/track/4' } } } } };
test('ordered partial resources retain metadata, dedupe and omit seed', () => {
 const result = parseRelated(payload, '123'); assert.deepEqual(result.tracks.map(t=>t.key), ['4','2']); assert.equal(result.omittedResourceCount,1); assert.equal(result.tracks[0].isrc,undefined); assert.equal(result.tracks[0].url,'https://www.shazam.com/track/4');
});
test('invalid IDs rejected before I/O', async () => {
 for(const id of ['', '-1', '+1', 'a', '1'.repeat(21)]) assert.throws(()=>validateId(id));
 assert.equal(validateId('123'),'123');
});
test('malformed relationship is failure, genuine empty list survives', () => {
 assert.throws(()=>parseRelated({},'123')); const empty=structuredClone(payload);empty.resources['shazam-song-lists']['track-similarities-id-123'].relationships.tracks.data=[];assert.deepEqual(parseRelated(empty,'123').tracks,[]);
});
test('parallel requests share acquisition and cached results', async () => {
 let acquisitions=0,calls=0; const client=createClient({acquire:async()=>{acquisitions++;await new Promise(r=>setTimeout(r,5));return 'token';},fetcher:async()=>{calls++;return response(200,payload);}});
 await Promise.all(Array.from({length:8},()=>client.getRelated('123','en-US','US')));assert.equal(acquisitions,1);assert.equal(calls,1);await client.getRelated('123','en-US','US');assert.equal(calls,1);
});
test('401 refreshes once and retries once', async () => {
 let acquisitions=0,calls=0;const client=createClient({acquire:async()=>`token${++acquisitions}`,fetcher:async()=>response(++calls===1?401:200,payload)});await client.getRelated('123','en-US','US');assert.equal(acquisitions,2);assert.equal(calls,2);
});
for(const status of [400,403,401]) test(`persistent ${status} does not loop`,async()=>{
 let calls=0;const client=createClient({acquire:async()=>'token',fetcher:async()=>{calls++;return response(status);}});await assert.rejects(client.getRelated('123','en-US','US'));assert.equal(calls,status===401?2:1);
});
test('transient failures retry within bounded count, not cached as empty',async()=>{
 let calls=0;const client=createClient({acquire:async()=>'token',sleep:async()=>{},fetcher:async()=>{calls++;return response(503);}});await assert.rejects(client.getRelated('123','en-US','US'));assert.equal(calls,3);
});
test('oversized response is rejected',async()=>{const client=createClient({acquire:async()=>'token',fetcher:async()=>new Response('x'.repeat(3*1024*1024+1))});await assert.rejects(client.getRelated('123','en-US','US'));});
test('long Retry-After fails rather than retrying early',async()=>{let calls=0;const client=createClient({acquire:async()=>'token',fetcher:async()=>{calls++;return response(429,{}, {'Retry-After':'120'});}});await assert.rejects(client.getRelated('123','en-US','US'));assert.equal(calls,1);});
test('expired in-memory token is renewed',async()=>{let time=0,acquisitions=0;const client=createClient({now:()=>time,acquire:async()=>`token${++acquisitions}`,fetcher:async()=>response(200,payload)});await client.getRelated('123','en-US','US');time=31*60*1000;await client.getRelated('123','en-US','US');assert.equal(acquisitions,2);});
test('retains artwork and linked Apple song metadata without inventing provider IDs',()=>{
 const body=structuredClone(payload);body.resources['shazam-songs']['4'].relationships={songs:{data:[{id:'77'}]}};
 body.resources.songs={'77':{attributes:{durationInMillis:201000,albumName:'Album',url:'https://music.apple.com/us/song/77'}}};
 body.resources['shazam-songs']['4'].attributes.artwork={url:'https://is1-ssl.mzstatic.com/image/{w}x{h}.jpg'};
 const card=parseRelated(body,'123').tracks[0];assert.equal(card.durationMs,201000);assert.equal(card.appleMusicUrl,'https://music.apple.com/us/song/77');assert.equal(card.images.coverart,'https://is1-ssl.mzstatic.com/image/600x600.jpg');assert.equal(card.deezerUrl,undefined);
});
test('late concurrent 401 uses refreshed generation instead of refreshing twice',async()=>{
 let acquisitions=0,calls=0;
 const client=createClient({acquire:async()=>`token${++acquisitions}`,fetcher:async(url,init)=>{
  calls++;if(init.headers.Authorization==='Bearer token1'){if(url.includes('id-456'))await new Promise(r=>setTimeout(r,20));return response(401);}
  const id=url.split('id-')[1],body=structuredClone(payload);body.resources['shazam-song-lists'][`track-similarities-id-${id}`]=body.resources['shazam-song-lists']['track-similarities-id-123'];return response(200,body);
 }});await Promise.all([client.getRelated('123'),client.getRelated('456')]);assert.equal(acquisitions,2);assert.equal(calls,4);
});
test('circuit rejects repeated transient failures then permits a half-open success',async()=>{
 let time=0,calls=0,working=false;const client=createClient({now:()=>time,sleep:async()=>{},acquire:async()=>'token',fetcher:async()=>{calls++;return response(working?200:503,payload);}});
 await assert.rejects(client.getRelated('123'));await assert.rejects(client.getRelated('456'));const before=calls;await assert.rejects(client.getRelated('789'));assert.equal(calls,before);time=31000;working=true;await client.getRelated('123');assert.equal(calls,before+1);
});
test('empty results expire sooner than positive results',async()=>{let time=0,calls=0;const empty=structuredClone(payload);empty.resources['shazam-song-lists']['track-similarities-id-123'].relationships.tracks.data=[];const client=createClient({now:()=>time,acquire:async()=>'token',fetcher:async()=>{calls++;return response(200,empty);}});await client.getRelated('123');time=59000;await client.getRelated('123');assert.equal(calls,1);time=61000;await client.getRelated('123');assert.equal(calls,2);});
test('invalid JSON and 204 remain unavailable, never empty success',async()=>{for(const status of [200,204]){const client=createClient({acquire:async()=>'token',fetcher:async()=>new Response(status===204?null:'invalid',{status})});await assert.rejects(client.getRelated('123'));}});
test('normalizes native Apple intent link while retaining its actual song URL',()=>{const body=structuredClone(payload);body.resources['shazam-songs']['4'].attributes.streaming={deeplink:'intent://music.apple.com/us/album/song/12?i=34#Intent;scheme=http;end'};assert.equal(parseRelated(body,'123').tracks[0].appleMusicUrl,'https://music.apple.com/us/album/song/12?i=34');});
test('SAP setup transient failures receive bounded recovery too',async(t)=>{
 let calls=0;t.mock.method(globalThis,'fetch',async()=>{calls++;return response(503);});await assert.rejects(createClient().getRelated('123'));assert.equal(calls,3);
});
test('repeated transient authentication outages open the circuit',async(t)=>{let calls=0;t.mock.method(globalThis,'fetch',async()=>{calls++;return response(503);});const client=createClient({now:()=>0});for(let n=0;n<6;n++)await assert.rejects(client.getRelated('123'));assert.equal(calls,15);});
test('bounded cache evicts large results before exceeding its byte budget',async()=>{
 let calls=0;const big=structuredClone(payload);big.resources['shazam-songs']['4'].attributes.title='x'.repeat(600000);
 const client=createClient({acquire:async()=>'token',fetcher:async url=>{calls++;const id=url.split('id-')[1],body=structuredClone(big);body.resources['shazam-song-lists'][`track-similarities-id-${id}`]=body.resources['shazam-song-lists']['track-similarities-id-123'];return response(200,body);}});
 for(let n=1000;n<1065;n++)await client.getRelated(String(n));await client.getRelated('1000');assert.equal(calls,66);
});
