-- Install in a dedicated Supabase project using the SQL Editor. No service key belongs in the app.
-- galaxy_private must NOT be added to the Data API's exposed schemas.
begin;
create schema if not exists galaxy_private;
revoke all on schema galaxy_private from public, anon;
grant usage on schema galaxy_private to authenticated;

create table galaxy_private.technicians (
  user_id uuid primary key references auth.users(id) on delete cascade,
  email text not null unique check (email=lower(email) and length(email)<=320),
  instance_id uuid not null,
  valid_until timestamptz not null,
  ip inet,
  port integer check (port between 1 and 65535),
  candidate_type text check (candidate_type in ('host','srflx','prflx')),
  updated_at timestamptz not null default now()
);
create table galaxy_private.sessions (
  id uuid primary key default gen_random_uuid(),
  guest_id uuid not null references auth.users(id) on delete cascade,
  technician_id uuid not null references galaxy_private.technicians(user_id) on delete cascade,
  status text not null default 'pending' check (status in ('pending','accepted','closed')),
  created_at timestamptz not null default now(),
  guest_seen timestamptz not null default now(),
  technician_seen timestamptz not null default now(),
  deadline timestamptz not null default now()+interval '1 hour',
  check (guest_id<>technician_id)
);
create unique index gc_one_technician_session on galaxy_private.sessions(technician_id) where status<>'closed';
create unique index gc_one_guest_session on galaxy_private.sessions(guest_id) where status<>'closed';
create table galaxy_private.signals (
  id bigint generated always as identity primary key,
  session_id uuid not null references galaxy_private.sessions(id) on delete cascade,
  sender_id uuid not null references auth.users(id) on delete cascade,
  kind text not null check (kind in ('offer','answer','candidate')),
  payload jsonb not null check (octet_length(payload::text)<=32768)
);
create index gc_signals_session on galaxy_private.signals(session_id,id);
create table galaxy_private.lookup_limits (
  user_id uuid primary key references auth.users(id) on delete cascade,
  window_start timestamptz not null default now(),
  attempts integer not null default 0
);
-- Private tables are never client-readable, including by anonymous Supabase Auth users.
alter table galaxy_private.technicians enable row level security;
alter table galaxy_private.sessions enable row level security;
alter table galaxy_private.signals enable row level security;
alter table galaxy_private.lookup_limits enable row level security;
revoke all on all tables in schema galaxy_private from public,anon,authenticated;
revoke all on all sequences in schema galaxy_private from public,anon,authenticated;

-- The privileged implementation is deliberately in an unexposed schema. It authenticates
-- every caller, binds email to auth.users (never user_metadata), and checks both session parties.
create function galaxy_private.gc_exchange(p_action text, p_data jsonb default '{}'::jsonb)
returns jsonb language plpgsql security definer set search_path='' as $$
declare
  actor uuid := auth.uid();
  account auth.users%rowtype;
  tech galaxy_private.technicians%rowtype;
  session galaxy_private.sessions%rowtype;
  instance uuid;
  sid uuid;
  signal jsonb;
  cleaned jsonb;
  kind text;
  description text;
  candidate text;
  fields text[];
  address inet;
  endpoint_port integer;
  route_kind text;
  after_id bigint;
  events jsonb;
  tries integer;
begin
  if actor is null then raise insufficient_privilege using message='Sign in before using Galaxy Control.'; end if;
  select * into account from auth.users where id=actor;
  if not found then raise insufficient_privilege using message='This account is no longer available.'; end if;
  if p_data is null or jsonb_typeof(p_data)<>'object' or octet_length(p_data::text)>35000 then
    raise invalid_parameter_value using message='Invalid request size.';
  end if;
  instance := nullif(p_data->>'instance_id','')::uuid;
  sid := nullif(p_data->>'session_id','')::uuid;

  if p_action='online' then
    if account.is_anonymous is true or account.email is null or account.email_confirmed_at is null then
      raise insufficient_privilege using message='A verified technician email is required.';
    end if;
    if instance is null then raise invalid_parameter_value using message='Missing app instance.'; end if;
    select * into tech from galaxy_private.technicians where user_id=actor for update;
    if found and tech.valid_until>now() and tech.instance_id<>instance then
      return jsonb_build_object('error','This technician is already online on another device.');
    end if;
    if found and tech.instance_id<>instance then
      update galaxy_private.sessions set status='closed' where technician_id=actor and status<>'closed';
    end if;
    insert into galaxy_private.technicians(user_id,email,instance_id,valid_until)
      values(actor,lower(account.email),instance,now()+interval '45 seconds')
      on conflict(user_id) do update set email=excluded.email,instance_id=excluded.instance_id,
        valid_until=excluded.valid_until,ip=null,port=null,candidate_type=null,updated_at=now();
    return jsonb_build_object('status','online');
  elsif p_action='request' then
    -- Exact email lookup only; no client can list the technician directory.
    insert into galaxy_private.lookup_limits(user_id,attempts) values(actor,1)
      on conflict(user_id) do update set
        attempts=case when galaxy_private.lookup_limits.window_start<now()-interval '1 minute' then 1 else galaxy_private.lookup_limits.attempts+1 end,
        window_start=case when galaxy_private.lookup_limits.window_start<now()-interval '1 minute' then now() else galaxy_private.lookup_limits.window_start end
      returning attempts into tries;
    if tries>10 then return jsonb_build_object('error','Too many requests. Please wait a minute.'); end if;
    select * into tech from galaxy_private.technicians where email=lower(trim(p_data->>'email')) and valid_until>now() for update;
    if not found then return jsonb_build_object('error','Technician is offline. Please try again shortly.'); end if;
    if tech.user_id=actor then return jsonb_build_object('error','You cannot connect to yourself.'); end if;
    update galaxy_private.sessions set status='closed' where (technician_id=tech.user_id or guest_id=actor) and status<>'closed'
      and (deadline<now() or guest_seen<now()-interval '45 seconds' or technician_seen<now()-interval '45 seconds'
        or (status='pending' and created_at<now()-interval '60 seconds'));
    if exists(select 1 from galaxy_private.sessions where (technician_id=tech.user_id or guest_id=actor) and status<>'closed') then
      return jsonb_build_object('error','A support session is already in progress. Please try again shortly.');
    end if;
    insert into galaxy_private.sessions(guest_id,technician_id) values(actor,tech.user_id) returning * into session;
    return jsonb_build_object('session_id',session.id,'status',session.status,'ip',host(tech.ip),'port',tech.port);
  end if;

  -- Technician polling also renews the online lease. A second app instance cannot replace it.
  if p_action='poll' and sid is null then
    select * into tech from galaxy_private.technicians where user_id=actor and instance_id=instance for update;
    if not found or tech.valid_until<now() then return jsonb_build_object('error','Your technician connection expired. Go online again.'); end if;
    update galaxy_private.technicians set valid_until=now()+interval '45 seconds',updated_at=now() where user_id=actor;
    select * into session from galaxy_private.sessions where technician_id=actor and status<>'closed' order by created_at limit 1;
    if not found then return jsonb_build_object('status','online','events','[]'::jsonb); end if;
    sid:=session.id;
  end if;
  if p_action='stop' and sid is null then
    update galaxy_private.technicians set valid_until=now() where user_id=actor and instance_id=instance;
    if found then update galaxy_private.sessions set status='closed' where technician_id=actor and status<>'closed'; end if;
    return jsonb_build_object('status','closed');
  end if;
  select * into session from galaxy_private.sessions where id=sid for update;
  if not found or actor not in (session.guest_id,session.technician_id) then
    raise insufficient_privilege using message='This support session is not yours.';
  end if;
  if actor=session.technician_id then
    select * into tech from galaxy_private.technicians where user_id=actor and instance_id=instance;
    if not found then raise insufficient_privilege using message='This technician app instance is no longer active.'; end if;
  end if;
  if session.deadline<now() or session.guest_seen<now()-interval '45 seconds' or session.technician_seen<now()-interval '45 seconds'
    or (session.status='pending' and session.created_at<now()-interval '60 seconds') then
    update galaxy_private.sessions set status='closed' where id=sid;
    session.status:='closed';
  end if;
  if p_action='stop' then
    update galaxy_private.sessions set status='closed' where id=sid;
    delete from galaxy_private.signals where session_id=sid;
    if actor=session.technician_id then update galaxy_private.technicians set valid_until=now() where user_id=actor and instance_id=instance; end if;
    return jsonb_build_object('status','closed');
  elsif p_action='accept' then
    if actor<>session.technician_id then raise insufficient_privilege using message='Only the technician can accept.'; end if;
    if session.status<>'pending' then return jsonb_build_object('error','This request is no longer pending.'); end if;
    update galaxy_private.sessions set status='accepted',technician_seen=now() where id=sid;
    return jsonb_build_object('status','accepted');
  elsif p_action='poll' then
    if session.status<>'closed' then
      if actor=session.guest_id then update galaxy_private.sessions set guest_seen=now() where id=sid;
      else
        update galaxy_private.sessions set technician_seen=now() where id=sid;
        update galaxy_private.technicians set valid_until=now()+interval '45 seconds',updated_at=now() where user_id=actor;
      end if;
    end if;
    after_id:=greatest(0,coalesce((p_data->>'after_id')::bigint,0));
    select coalesce(jsonb_agg(jsonb_build_object('id',id,'signal',payload) order by id),'[]'::jsonb) into events
      from galaxy_private.signals where session_id=sid and sender_id<>actor and id>after_id;
    return jsonb_build_object('session_id',sid,'status',session.status,'events',events);
  elsif p_action='signal' then
    if session.status<>'accepted' then raise insufficient_privilege using message='This session has not been accepted or has ended.'; end if;
    signal:=p_data->'signal';kind:=signal->>'type';
    if kind in ('offer','answer') then
      if (kind='offer')<>(actor=session.guest_id) then raise insufficient_privilege using message='Invalid description sender.'; end if;
      if exists(select 1 from galaxy_private.signals s where s.session_id=sid and s.kind=signal->>'type') then
        raise invalid_parameter_value using message='Duplicate session description.';
      end if;
      description:=signal->>'sdp';
      if description is null or length(description)>30000 or left(description,3)<>'v=0'
        or position('m=application ' in description)=0 or position('a=fingerprint:sha-256 ' in lower(description))=0
        or position(' typ relay' in lower(description))>0 or description ~ E'(^|\n)m=(?!application )' then
        raise invalid_parameter_value using message='Only direct data-channel descriptions are allowed.';
      end if;
      cleaned:=jsonb_build_object('type',kind,'sdp',description);
    elsif kind='candidate' then
      if (select count(*) from galaxy_private.signals s where s.session_id=sid and s.sender_id=actor and s.kind='candidate')>=128 then
        raise invalid_parameter_value using message='Too many connection candidates.';
      end if;
      candidate:=signal->'candidate'->>'candidate';
      if candidate is null or length(candidate)>2048 or left(candidate,10)<>'candidate:' or candidate !~ ' typ (host|srflx|prflx)( |$)' then
        raise invalid_parameter_value using message='Only direct ICE candidates are allowed.';
      end if;
      if length(signal->'candidate'->>'sdpMid')>32 or coalesce((signal->'candidate'->>'sdpMLineIndex')::int,0) not between 0 and 8 then
        raise invalid_parameter_value using message='Invalid ICE index.';
      end if;
      cleaned:=jsonb_build_object('type',kind,'candidate',jsonb_build_object('candidate',candidate,
        'sdpMid',signal->'candidate'->'sdpMid','sdpMLineIndex',signal->'candidate'->'sdpMLineIndex'));
      -- Refresh the technician's email → IP/port mapping from actual negotiated endpoints.
      -- mDNS-only host candidates have no literal IP and are still exchanged for WebRTC.
      if actor=session.technician_id then
        fields:=string_to_array(candidate,' ');route_kind:=fields[8];
        begin address:=fields[5]::inet;endpoint_port:=fields[6]::int;
        exception when invalid_text_representation or numeric_value_out_of_range then address:=null;end;
        if address is not null and endpoint_port between 1 and 65535 then
          update galaxy_private.technicians set ip=address,port=endpoint_port,candidate_type=route_kind,updated_at=now()
            where user_id=actor and (candidate_type is null or route_kind='srflx' or candidate_type<>'srflx');
        end if;
      end if;
    else raise invalid_parameter_value using message='Screen, keyboard and mouse data must use direct P2P, never Supabase.';
    end if;
    insert into galaxy_private.signals(session_id,sender_id,kind,payload) values(sid,actor,kind,cleaned);
    return jsonb_build_object('status','sent');
  end if;
  raise invalid_parameter_value using message='Unsupported Galaxy Control operation.';
end;
$$;
revoke all on function galaxy_private.gc_exchange(text,jsonb) from public,anon,authenticated;
grant execute on function galaxy_private.gc_exchange(text,jsonb) to authenticated;

-- Thin, non-privileged Data API wrapper. No tables or privileged functions are public.
create function public.gc_exchange(p_action text,p_data jsonb default '{}'::jsonb)
returns jsonb language sql security invoker set search_path=''
as $$ select galaxy_private.gc_exchange(p_action,p_data); $$;
revoke all on function public.gc_exchange(text,jsonb) from public,anon,authenticated;
grant execute on function public.gc_exchange(text,jsonb) to authenticated;
notify pgrst,'reload schema';
commit;
