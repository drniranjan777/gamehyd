import { DynamicModule, Module } from '@nestjs/common';
import { AuthController } from './auth/auth.controller';
import { AuthService } from './auth/auth.service';
import { TokenService } from './auth/token.service';
import {
  InventoryController,
  MapsController,
  ReportsController,
} from './catalog/catalog.controller';
import { AuthGuard, RateLimitGuard, ServerSignatureGuard } from './common/guards';
import { APP_CONFIG, AppConfig } from './config';
import { DatabaseModule } from './database/database.module';
import { HealthController } from './health/health.controller';
import {
  InternalController,
  MatchesController,
  MatchmakingController,
} from './matchmaking/matchmaking.controller';
import { MatchmakingService } from './matchmaking/matchmaking.service';
import { ResultsService } from './matchmaking/results.service';
import { PlayerController } from './player/player.controller';

@Module({})
export class AppModule {
  static forConfig(config: AppConfig): DynamicModule {
    const configModule: DynamicModule = {
      module: class ConfigModule {},
      global: true,
      providers: [{ provide: APP_CONFIG, useValue: config }],
      exports: [APP_CONFIG],
    };
    return {
      module: AppModule,
      imports: [configModule, DatabaseModule],
      controllers: [
        HealthController,
        AuthController,
        PlayerController,
        MapsController,
        InventoryController,
        ReportsController,
        MatchmakingController,
        MatchesController,
        InternalController,
      ],
      providers: [
        TokenService,
        AuthService,
        MatchmakingService,
        ResultsService,
        AuthGuard,
        RateLimitGuard,
        ServerSignatureGuard,
      ],
    };
  }
}
