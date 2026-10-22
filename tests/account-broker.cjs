const assert=require('node:assert/strict');
(async()=>{
 const {createAccountBroker}=await import('../supabase/functions/connect-supabase/handler.mjs');
 const base='https://connect.example.test/functions/v1/connect-supabase/';let calls=0;
 const broker=createAccountBroker({clientId:'test-client',clientSecret:'test-secret',baseUrl:base,fetchImpl:async(url,options)=>{calls++;assert.equal(url,'https://api.supabase.com/v1/oauth/token');assert.equal(options.redirect,'error');assert.match(options.headers.Authorization,/^Basic /);return Response.json({access_token:'mock-account-authorization',refresh_token:'never-return-this'});}});
 const verifier='a'.repeat(43);const challenge=Buffer.from(await crypto.subtle.digest('SHA-256',new TextEncoder().encode(verifier))).toString('base64url');const state='b'.repeat(64);
 let r=await broker(new Request(base+'start?port=49152&state='+state+'&challenge='+challenge));assert.equal(r.status,302);
 const authorize=new URL(r.headers.get('location'));assert.equal(authorize.origin,'https://api.supabase.com');assert.equal(authorize.searchParams.get('code_challenge_method'),'S256');
 const ticket=authorize.searchParams.get('state');
 r=await broker(new Request(base+'callback?code=test-code&state='+encodeURIComponent(ticket)));assert.equal(r.status,302);
 const callback=new URL(r.headers.get('location'));assert.equal(callback.origin,'http://127.0.0.1:49152');assert.equal(callback.searchParams.get('state'),state);assert.equal(callback.searchParams.get('access_token'),null);
 r=await broker(new Request(base+'exchange',{method:'POST',body:JSON.stringify({ticket,code:'test-code',verifier})}));assert.equal(r.status,200);assert.deepEqual(await r.json(),{access_token:'mock-account-authorization'});assert.equal(calls,1);
 r=await broker(new Request(base+'exchange',{method:'POST',body:JSON.stringify({ticket,code:'test-code',verifier:'c'.repeat(43)})}));assert.equal(r.status,400);assert.equal(calls,1);
 r=await broker(new Request(base+'callback?code=code&state='+encodeURIComponent(ticket+'bad')));assert.equal(r.status,400);
 r=await broker(new Request(base+'start?port=443&state='+state+'&challenge='+challenge));assert.equal(r.status,400);
 const expired=createAccountBroker({clientId:'id',clientSecret:'test-secret',baseUrl:base,now:()=>Date.now()+700000});r=await expired(new Request(base+'callback?code=code&state='+encodeURIComponent(ticket)));assert.equal(r.status,400);
 const missing=createAccountBroker({});assert.equal((await missing(new Request(base+'status'))).status,503);
 console.log('PASS: PKCE, signed expiring state, fixed loopback callback, token isolation, invalid verifier/signature rejection and missing publisher configuration.');
})().catch(e=>{console.error(e);process.exitCode=1;});
