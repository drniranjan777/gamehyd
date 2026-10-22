import { DynamicModule, Module } from '@nestjs/common';
import { AdminAuthService, AdminGuard } from './admin/admin-auth';
import { AdminAuthController, AdminController } from './admin/admin.controller';
import { AdminEconomyController } from './admin/admin-economy.controller';
import { AdminOpsController } from './admin/admin-ops.controller';
import { AdminPlayersController } from './admin/admin-players.controller';
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
import { GameConfigService } from './store/game-config.service';
import { JobsService } from './store/jobs.service';
import {
  AccountDeletionController,
  PublicController,
  StoreController,
  WebhooksController,
} from './store/store.controller';
import { StoreService } from './store/store.service';
import { WebhooksService } from './store/webhooks.service';

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
        StoreController,
        WebhooksController,
        AccountDeletionController,
        PublicController,
        AdminAuthController,
        AdminController,
        AdminPlayersController,
        AdminEconomyController,
        AdminOpsController,
      ],
      providers: [
        TokenService,
        AuthService,
        MatchmakingService,
        ResultsService,
        GameConfigService,
        StoreService,
        WebhooksService,
        JobsService,
        AdminAuthService,
        AdminGuard,
        AuthGuard,
        RateLimitGuard,
        ServerSignatureGuard,
      ],
    };
  }
}
