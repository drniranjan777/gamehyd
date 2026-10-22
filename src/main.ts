import 'reflect-metadata';
import { NestFactory } from '@nestjs/core';
import type { NestExpressApplication } from '@nestjs/platform-express';
import { AppModule } from './app.module';
import { configureApp } from './app.setup';
import { loadConfig } from './config';

async function bootstrap(): Promise<void> {
  const config = loadConfig();
  const app = await NestFactory.create<NestExpressApplication>(AppModule.forConfig(config), {
    rawBody: true,
    logger: ['error', 'warn'],
  });
  configureApp(app, config);
  app.enableShutdownHooks();
  await app.listen(config.PORT);
  console.log(JSON.stringify({ level: 'info', msg: 'api listening', port: config.PORT }));
}

bootstrap().catch((err: Error) => {
  console.error(JSON.stringify({ level: 'fatal', msg: err.message }));
  process.exit(1);
});
