import { createAccountBroker } from './handler.mjs';
Deno.serve(createAccountBroker({
  clientId:Deno.env.get('GALAXY_SUPABASE_CLIENT_ID'),
  clientSecret:Deno.env.get('GALAXY_SUPABASE_CLIENT_SECRET'),
  baseUrl:Deno.env.get('GALAXY_SUPABASE_BROKER_URL'),
}));
