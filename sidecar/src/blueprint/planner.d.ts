import type { PlanResult } from '../kb/types.js';

export interface PlanBlueprintOptions {
  beltType?: number;
  insertType?: number;
  recycleMode?: number;
  rows?: number;
  stackSize?: number;
  floor?: number;
  stationPiler?: number;
  name?: string;
  author?: string;
  version?: string;
  attributes?: string;
  description?: string;
  gameVersion?: string;
}

export interface BlueprintStage {
  recipeId: number;
  recipeName: string;
  primaryItemId: number;
  primaryItem: string;
  buildingId?: number;
  buildingName?: string;
  buildingCount: number;
  craftsPerMin: number;
  outputsPerMin: { itemId: number; item: string; amount: number }[];
}

export interface PlanBlueprintMeta {
  rows: number;
  requestedRows: number;
  width: number;
  height: number;
  buildings: number;
  machines: number;
  stages: BlueprintStage[];
  raw: { itemId: number; item: string; amount: number }[];
  byproducts: { itemId: number; item: string; amount: number }[];
  warnings: string[];
  expectedByBuilding: Record<string, number>;
}

export interface PlanBlueprintResult {
  text: string;
  meta: PlanBlueprintMeta;
}

export function planBlueprint(plan: PlanResult, options?: PlanBlueprintOptions): PlanBlueprintResult;
