// Fails when src/schema.ts no longer matches openapi.yaml (CI drift check).
import { readFile } from 'node:fs/promises';
import openapiTS, { astToString } from 'openapi-typescript';
import prettier from 'prettier';

const root = new URL('../', import.meta.url);
const ast = await openapiTS(new URL('openapi.yaml', root));
const options = (await prettier.resolveConfig(new URL('src/schema.ts', root))) ?? {};
const fresh = await prettier.format(astToString(ast), { ...options, parser: 'typescript' });
const committed = await readFile(new URL('src/schema.ts', root), 'utf8');

// The CLI adds a banner comment; compare from the first export onwards.
const body = (s) => s.slice(s.indexOf('export '));
if (body(fresh) !== body(committed)) {
  console.error('src/schema.ts is out of date: run `pnpm --filter @rwbr/api-contracts generate`');
  process.exit(1);
}
console.log('api-contracts: generated types match openapi.yaml');
