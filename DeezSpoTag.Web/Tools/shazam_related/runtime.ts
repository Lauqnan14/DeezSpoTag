// SAP implementation: MIT, Lakr233/AssppWeb / ApplePackage. See LICENSE.
import { createHandshake } from './sap/handshake';
import { createSigner } from './sap/signer';
import { createInterface } from 'node:readline';
import { pathToFileURL } from 'node:url';

const MAX_BODY = 3 * 1024 * 1024;
const hosts = new Set(['s.mzstatic.com', 'fpinit.itunes.apple.com', 'sf-api-token-service.itunes.apple.com', 'cdn.shazam.com']);
class ProviderError extends Error { constructor(public code: string) { super(code); } }
export function validateId(id: string) { if(typeof id!=='string'||!/^\d{1,20}$/.test(id))throw new ProviderError('invalid_id');return id; }
function safeUrl(value: any) {
 if(typeof value!=='string')return undefined;
 try { const url=new URL(value);return url.protocol==='https:' && /(^|\.)(shazam\.com|apple\.com|spotify\.com|deezer\.com|mzstatic\.com)$/.test(url.hostname)&&!url.username&&!url.password ? url.href:undefined; } catch{return undefined;}
}
export function parseRelated(body: any, id: string) {
 const resources=body?.resources;
 const refs=resources?.['shazam-song-lists']?.[`track-similarities-id-${id}`]?.relationships?.tracks?.data;
 if(!Array.isArray(refs)||refs.length>1000)throw new ProviderError('invalid_response');
 const tracks:any[]=[], seen=new Set<string>();let omittedResourceCount=0;
 for(const ref of refs){
  const key=ref?.id;if(typeof key!=='string'||key===id||seen.has(key))continue;seen.add(key);
  const song=resources?.['shazam-songs']?.[key];if(!song?.attributes){omittedResourceCount++;continue;}
  const a=song.attributes;
  const linkedId=song.relationships?.songs?.data?.[0]?.id;
  const linked=typeof linkedId==='string'?resources?.songs?.[linkedId]?.attributes:undefined;
  const streamLink=a.streaming?.deeplink;
  const nativeAppleLink=typeof streamLink==='string'&&streamLink.startsWith('intent://music.apple.com/')
   ? 'https://'+streamLink.slice('intent://'.length).split('#Intent;')[0] : streamLink;
  const appleUrl=safeUrl(a.appleMusicUrl)??safeUrl(linked?.url)??safeUrl(nativeAppleLink);
  const title=a.title??a.name;const artist=a.artist??a.artistName??a.subtitle;
  if(typeof title!=='string'||typeof artist!=='string'){omittedResourceCount++;continue;}
  // Keep native identity distinct from provider playback identities; absent values stay absent.
  tracks.push({key,title,subtitle:artist,isrc:typeof a.isrc==='string'?a.isrc:undefined,
   durationMs:Number.isFinite(a.durationInMillis)?a.durationInMillis:Number.isFinite(a.durationMs)?a.durationMs:Number.isFinite(linked?.durationInMillis)?linked.durationInMillis:undefined,
   album:typeof a.albumName==='string'?a.albumName:typeof linked?.albumName==='string'?linked.albumName:undefined,url:safeUrl(a.url??a.webUrl),
   appleMusicUrl:appleUrl,spotifyUrl:safeUrl(a.spotifyUrl),deezerUrl:safeUrl(a.deezerUrl),
   images:{coverart:safeUrl(a.artwork?.url?.replaceAll('{w}','600').replaceAll('{h}','600')??a.images?.coverArt??a.images?.coverart)}});
 }
 return {tracks,omittedResourceCount};
}
async function boundedText(response: Response) {
 if(Number(response.headers.get('content-length'))>MAX_BODY)throw new ProviderError('response_too_large');
 if(!response.body)return '';
 const reader=response.body.getReader();let length=0;const parts:Uint8Array[]=[];
 try{while(true){const {done,value}=await reader.read();if(done)break;length+=value.length;if(length>MAX_BODY)throw new ProviderError('response_too_large');parts.push(value);}}
 finally{await reader.cancel().catch(()=>{});}
 return Buffer.concat(parts).toString('utf8');
}
async function nativeFetch(url:string, init:any={}) {
 if(!hosts.has(new URL(url).hostname))throw new ProviderError('invalid_host');
 return fetch(url,{...init,redirect:'error'});
}
function plist(xml:string,key:string){const m=xml.match(new RegExp(`<key>\\s*${key}\\s*</key>\\s*<data>([\\s\\S]*?)</data>`));if(!m)throw new ProviderError('setup_failed');return new Uint8Array(Buffer.from(m[1].replace(/\s/g,''),'base64'));}
async function acquireToken(signal:AbortSignal) {
 async function request(url:string,init:any={}){
  for(let attempt=0;;attempt++){
   const r=await nativeFetch(url,{...init,signal:AbortSignal.any([signal,AbortSignal.timeout(15000)])});
   if(r.ok)return boundedText(r);
   await r.body?.cancel();
   if(r.status!==429&&r.status<500)throw new ProviderError('token_unavailable');
   if(attempt>=2)throw new ProviderError('token_temporarily_unavailable');
   const header=r.headers.get('retry-after');
   const delay=header ? /^\d+$/.test(header)?Number(header)*1000:Math.max(0,Date.parse(header)-Date.now()):250*2**attempt;
   if(!Number.isFinite(delay)||delay>=15000||signal.aborted)throw new ProviderError('token_temporarily_unavailable');
   await new Promise<void>((resolve,reject)=>{
    const timer=setTimeout(done,delay);
    function done(){signal.removeEventListener('abort',abort);resolve();}
    function abort(){clearTimeout(timer);reject(new ProviderError('upstream_timeout'));}
    signal.addEventListener('abort',abort,{once:true});if(signal.aborted)abort();
   });
  }
 }
 const cert=plist(await request('https://s.mzstatic.com/sap/setupCert.plist'),'sign-sap-setup-cert');
 const handshake=await createHandshake(cert);
 const body=`<?xml version="1.0"?><plist version="1.0"><dict><key>sign-sap-setup-buffer</key><data>${Buffer.from(handshake.request).toString('base64')}</data></dict></plist>`;
 const setup=await request('https://fpinit.itunes.apple.com/v1/signSapSetup/legacy',{method:'POST',headers:{'Content-Type':'application/x-apple-plist'},body});
 const signer=await createSigner(await handshake.complete(plist(setup,'sign-sap-setup-buffer')));
 const app='com.shazam.android',store='143441-1,29',timestamp=new Date().toISOString().replace(/\.\d{3}Z$/,'Z');
 const signature=await signer.sign(new TextEncoder().encode(store+app+timestamp+app));
 const data=JSON.parse(await request(`https://sf-api-token-service.itunes.apple.com/apiToken?clientClass=apple&clientId=${app}`,{headers:{
 'X-Apple-Store-Front':store,'X-Apple-Client-Application':app,'X-Request-Timestamp':timestamp,'X-Apple-Tz':'0',
 'Accept-Language':'en-US','User-Agent':'com.shazam.android/16.64.0 Android/15','X-Apple-ActionSignature':Buffer.from(signature).toString('base64')}}));
 if(typeof data.token!=='string'||!data.token)throw new ProviderError('token_unavailable');
 return data.token;
}
export function createClient(options:any={}) {
 const now=options.now??Date.now,acquire=options.acquire??acquireToken,fetcher=options.fetcher??nativeFetch;
 const sleep=options.sleep??((ms:number,signal:AbortSignal)=>new Promise((resolve,reject)=>{const timer=setTimeout(done,ms);function done(){signal.removeEventListener('abort',abort);resolve(undefined);}function abort(){clearTimeout(timer);reject(new ProviderError('upstream_timeout'));}signal.addEventListener('abort',abort,{once:true});if(signal.aborted)abort();}));
 let token:string|undefined,issued=0,acquiring:Promise<string>|undefined,active=0,failures=0,openUntil=0,probing=false;
 const cache=new Map<string,any>(), pending=new Map<string,Promise<any>>();
 let cacheBytes=0;
 function removeCached(key:string){const old=cache.get(key);if(old){cacheBytes-=old.bytes;cache.delete(key);}}
 async function getToken(signal:AbortSignal){
  if(token&&now()-issued<600000)return token;
  if(!acquiring)acquiring=Promise.resolve().then(()=>acquire(signal)).then(t=>{token=t;issued=now();return t;}).finally(()=>{acquiring=undefined;});
  return acquiring;
 }
 async function retrieve(id:string,language:string,country:string){
  if(active>=4)throw new ProviderError('upstream_busy');
  if(openUntil>now()||openUntil>0&&probing)throw new ProviderError('upstream_unavailable');
  if(openUntil>0)probing=true;active++;
  const signal=AbortSignal.timeout(30000);let refresh=false,retries=0;
  try{while(true){
   const used=await getToken(signal);
   const r=await fetcher(`https://cdn.shazam.com/song/v2/${language}/${country}/android/shazam-song-lists/track-similarities-id-${id}`,{headers:{Authorization:`Bearer ${used}`,'X-Apple-Client-Application':'com.shazam.android','Accept-Language':language,'User-Agent':'Shazam/16.64.0 AMS/1'},signal:AbortSignal.any([signal,AbortSignal.timeout(15000)])});
   if(r.status===401&&!refresh){await r.body?.cancel();refresh=true;if(token===used)token=undefined;await getToken(signal);continue;}
   if(r.status===429||r.status>=500){
    await r.body?.cancel();failures++;if(failures>=5)openUntil=now()+30000;
    const header=r.headers.get('retry-after');const delay=header ? /^\d+$/.test(header)?Number(header)*1000:Math.max(0,Date.parse(header)-now()):250*2**retries;
    if(retries++>=2||!Number.isFinite(delay)||delay>=15000||signal.aborted)throw new ProviderError('upstream_unavailable');await sleep(delay,signal);continue;
   }
   if(!r.ok||r.status===204){await r.body?.cancel();throw new ProviderError('upstream_unavailable');}
   let body;try{body=JSON.parse(await boundedText(r));}catch{throw new ProviderError('invalid_response');}
   const result=parseRelated(body,id);failures=0;openUntil=0;return result;
  }}catch(e){if(!(e instanceof ProviderError)||e.code==='token_temporarily_unavailable'||e.code==='upstream_timeout'){failures++;if(failures>=5)openUntil=now()+30000;}throw e instanceof ProviderError?e:new ProviderError('upstream_unavailable');}
  finally{active--;probing=false;}
 }
 return {async getRelated(id:string,language='en-US',country='US'){
  validateId(id);if(!/^[a-z]{2}-[A-Z]{2}$/.test(language)||!/^[A-Z]{2}$/.test(country))throw new ProviderError('invalid_locale');
  const key=`${id}:${language}:${country}`,entry=cache.get(key);if(entry&&entry.expires>now())return entry.result;
  if(!pending.has(key)){const work=retrieve(id,language,country).then(result=>{
   const bytes=Buffer.byteLength(JSON.stringify(result));removeCached(key);
   while(cache.size&&(cache.size>=2000||cacheBytes+bytes>32*1024*1024))removeCached(cache.keys().next().value!);
   cache.set(key,{result,bytes,expires:now()+(result.tracks.length?1800000:60000)});cacheBytes+=bytes;return result;
  }).finally(()=>pending.delete(key));pending.set(key,work);}
  return pending.get(key);
 }};
}
// Private child protocol. Authentication never leaves this process.
if(process.argv[1]&&import.meta.url===pathToFileURL(process.argv[1]).href){
 const client=createClient();let active=0;
 const input=createInterface({input:process.stdin,crlfDelay:Infinity});
 input.on('line',async line=>{
  let id:any;
  try{if(Buffer.byteLength(line)>4096)throw new ProviderError('invalid_request');const request=JSON.parse(line);id=request.requestId;if(typeof id!=='string'||id.length>64)throw new ProviderError('invalid_request');if(active>=32)throw new ProviderError('upstream_busy');active++;
   try{const result=await client.getRelated(request.shazamId,request.language,request.country);process.stdout.write(JSON.stringify({requestId:id,ok:true,...result})+'\n');}finally{active--;}
  }catch(e){process.stdout.write(JSON.stringify({requestId:id,ok:false,errorCode:e instanceof ProviderError?e.code:'upstream_unavailable'})+'\n');}
 });
 input.on('close',()=>process.exit(0));
}
