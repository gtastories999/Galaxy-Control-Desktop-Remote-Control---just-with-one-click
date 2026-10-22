const encoder=new TextEncoder();
const b64=b=>btoa(String.fromCharCode(...new Uint8Array(b))).replaceAll('+','-').replaceAll('/','_').replace(/=+$/,'');
const unb64=s=>Uint8Array.from(atob(s.replaceAll('-','+').replaceAll('_','/')),c=>c.charCodeAt(0));
export function createAccountBroker({clientId,clientSecret,baseUrl,fetchImpl=fetch,now=()=>Date.now()}) {
  const ready=!!clientId&&!!clientSecret&&!!baseUrl;
  const root=ready?new URL(baseUrl.replace(/\/$/,'')+'/'):null;
  const headers={'Cache-Control':'no-store','Referrer-Policy':'no-referrer','Content-Security-Policy':"default-src 'none'; frame-ancestors 'none'",'X-Content-Type-Options':'nosniff'};
  const json=(body,status=200)=>Response.json(body,{status,headers});
  const error=(status=400)=>json({error:'Account authorization could not be completed. Return to Galaxy Control and retry.'},status);
  const key=()=>crypto.subtle.importKey('raw',encoder.encode(clientSecret),{name:'HMAC',hash:'SHA-256'},false,['sign','verify']);
  async function ticket(data){const payload=b64(encoder.encode(JSON.stringify(data)));return payload+'.'+b64(await crypto.subtle.sign('HMAC',await key(),encoder.encode(payload)));}
  async function verify(s){
    if(typeof s!=='string'||s.length>4096)throw Error('ticket');const parts=s.split('.');
    if(parts.length!==2||!await crypto.subtle.verify('HMAC',await key(),unb64(parts[1]),encoder.encode(parts[0])))throw Error('signature');
    const data=JSON.parse(new TextDecoder().decode(unb64(parts[0])));
    if(!Number.isInteger(data.port)||data.port<1024||data.port>65535||!/^[a-f0-9]{64}$/.test(data.state)||!/^[a-zA-Z0-9_-]{43}$/.test(data.challenge)||!Number.isFinite(data.created)||now()-data.created>600000||data.created>now()+30000)throw Error('expiry');
    return data;
  }
  return async request=>{
    try{
      if(!ready)return error(503);
      const url=new URL(request.url);if(!url.pathname.startsWith(root.pathname))return error(404);
      const route=url.pathname.slice(root.pathname.length);
      if(request.method==='GET'&&route==='status')return json({ready:true});
      if(request.method==='GET'&&route==='start'){
        const data={port:Number(url.searchParams.get('port')),state:url.searchParams.get('state'),challenge:url.searchParams.get('challenge'),created:now()};
        const state=await ticket(data);await verify(state);
        const authorize=new URL('https://api.supabase.com/v1/oauth/authorize');
        for(const [k,v] of Object.entries({client_id:clientId,response_type:'code',redirect_uri:new URL('callback',root).href,state,code_challenge:data.challenge,code_challenge_method:'S256'}))authorize.searchParams.set(k,v);
        return new Response(null,{status:302,headers:{...headers,Location:authorize.href}});
      }
      if(request.method==='GET'&&route==='callback'){
        const signed=url.searchParams.get('state'),data=await verify(signed);
        const target=new URL(`http://127.0.0.1:${data.port}/callback`);target.searchParams.set('state',data.state);
        const code=url.searchParams.get('code');if(code&&code.length<=2048){target.searchParams.set('code',code);target.searchParams.set('ticket',signed);}else target.searchParams.set('error','authorization_denied');
        return new Response(null,{status:302,headers:{...headers,Location:target.href}});
      }
      if(request.method==='POST'&&route==='exchange'){
        if(Number(request.headers.get('content-length'))>16384)return error(413);
        const text=await request.text();if(text.length>16384)return error(413);const body=JSON.parse(text);
        const data=await verify(body.ticket);
        if(typeof body.verifier!=='string'||!/^[a-zA-Z0-9_-]{43,128}$/.test(body.verifier)||b64(await crypto.subtle.digest('SHA-256',encoder.encode(body.verifier)))!==data.challenge||typeof body.code!=='string'||body.code.length>2048)return error();
        const response=await fetchImpl('https://api.supabase.com/v1/oauth/token',{method:'POST',redirect:'error',headers:{'Content-Type':'application/x-www-form-urlencoded',Authorization:'Basic '+btoa(clientId+':'+clientSecret)},body:new URLSearchParams({grant_type:'authorization_code',code:body.code,redirect_uri:new URL('callback',root).href,code_verifier:body.verifier}),signal:AbortSignal.timeout(20000)});
        if(!response.ok)return error(401);const tokens=await response.json();
        if(typeof tokens.access_token!=='string'||!tokens.access_token||tokens.access_token.length>8192)return error(502);
        // Refresh tokens and app credentials never leave this exchange service.
        return json({access_token:tokens.access_token});
      }
      return error(404);
    }catch{return error();}
  };
}
