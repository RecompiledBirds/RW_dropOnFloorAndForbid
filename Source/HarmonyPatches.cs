using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Threading.Tasks;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace dropOnFloorAndForbid.Source
{
    [StaticConstructorOnStartup]
    public static class HarmonyPatches
    {
        static readonly FieldInfo f_someField = AccessTools.Field(typeof(BillStoreModeDef), nameof(BillStoreModeDef.defName));
		static readonly MethodInfo CalculateIngredients = typeof(Toils_Recipe).GetMethod("CalculateIngredients", BindingFlags.NonPublic | BindingFlags.Static);
		static readonly MethodInfo CalculateDominantIngredient = typeof(Toils_Recipe).GetMethod("CalculateDominantIngredient", BindingFlags.NonPublic | BindingFlags.Static);
		static readonly MethodInfo ConsumeIngredients = typeof(Toils_Recipe).GetMethod("ConsumeIngredients", BindingFlags.NonPublic | BindingFlags.Static);

		static HarmonyPatches()
        {
            Harmony harmony = new Harmony("DOFAF.BillPatch");
            harmony.Patch(typeof(Toils_Recipe).GetMethod(nameof(Toils_Recipe.FinishRecipeAndStartStoringProduct)), new HarmonyMethod(typeof(HarmonyPatches), nameof(ToilPatch)));

            //if (ModsConfig.ActiveModsInLoadOrder.FirstOrFallback(modMetaData => modMetaData.GetPublishedFileId().m_PublishedFileId == 935982361) is ModMetaData data)
            //{
            //    harmony.Patch(Type.GetType("ImprovedWorkbenches.Bill_Production_DoConfigInterface_Detour, ImprovedWorkbenches").GetMethod("Postfix"), new HarmonyMethod(typeof(HarmonyPatches), nameof(Skip)));
            //}
        }

        //static void Prefix()
        //{
        //    Put Button Patch Here?
        //    probably needs a transpiler
        //}

        static bool ToilPatch(ref Toil __result)
        {
			Toil toil = new Toil();
            
			toil.initAction = () =>
            {
                Pawn actor = toil.actor;
                Job curJob = actor.jobs.curJob;
                JobDriver_DoBill jobDriver_DoBill = (JobDriver_DoBill)actor.jobs.curDriver;

                List<Thing> ingredients = (List<Thing>)CalculateIngredients.Invoke(null, new object[] { curJob, actor });
                Thing dominantIngredient = (Thing)CalculateDominantIngredient.Invoke(null, new object[] { curJob, ingredients });

                ThingStyleDef style = SetStyle(curJob);
                List<Thing> products = GetProducts(actor, curJob, jobDriver_DoBill, ingredients, dominantIngredient, style);

                EarnSkillFromJob(actor, curJob, jobDriver_DoBill);
                ConsumeIngredients.Invoke(null, new object[] { ingredients, curJob.RecipeDef, actor.Map });
                curJob.bill.Notify_IterationCompleted(actor, ingredients);
                RecordsUtility.Notify_BillDone(actor, products);
                MakeTale(actor, curJob, products);
                NotifyQuestManager(actor, products);

                //End if bill has no products
                if (products.Count == 0)
                {
                    actor.jobs.EndCurrentJob(JobCondition.Succeeded, true, true);
                    return;
                }

                //Normal DropOnFloor mode
                if (curJob.bill.GetStoreMode() == BillStoreModeDefOf.DropOnFloor)
                {
                    TryPlaceThings(actor, products);
                    actor.jobs.EndCurrentJob(JobCondition.Succeeded, true, true);
                    return;
                }

                //Custom new special forbidden drop
                if (curJob.bill.GetStoreMode() == BillDefOf.forbiddenDrop)
                {
                    TryPlaceThings(actor, products, (thing, integer) => thing.SetForbidden(true));
                    actor.jobs.EndCurrentJob(JobCondition.Succeeded, true, true);
                    return;
                }

                if (products.Count > 1)
                {
                    for (int k = 1; k < products.Count; k++)
                    {
                        if (!GenPlace.TryPlaceThing(products[k], actor.Position, actor.Map, ThingPlaceMode.Near, null, null, null, 1))
                        {
                            Log.Error(string.Format("{0} could not drop recipe product {1} near {2}", actor, products[k], actor.Position));
                        }
                    }
                }
                IntVec3 invalid = IntVec3.Invalid;
                if (curJob.bill.GetStoreMode() == BillStoreModeDefOf.BestStockpile)
                {
                    StoreUtility.TryFindBestBetterStoreCellFor(products[0], actor, actor.Map, StoragePriority.Unstored, actor.Faction, out invalid, true);
                }
                else if (curJob.bill.GetStoreMode() == BillStoreModeDefOf.SpecificStockpile)
                {
                    StoreUtility.TryFindBestBetterStoreCellForIn(products[0], actor, actor.Map, StoragePriority.Unstored, actor.Faction, curJob.bill.GetSlotGroup(), out invalid, true);
                }
                else
                {
                    Log.ErrorOnce("Unknown store mode", 9158246);
                }
                if (!invalid.IsValid)
                {
                    if (!GenPlace.TryPlaceThing(products[0], actor.Position, actor.Map, ThingPlaceMode.Near, null, null, null, 1))
                    {
                        Log.Error(string.Format("Bill doer could not drop product {0} near {1}", products[0], actor.Position));
                    }
                    actor.jobs.EndCurrentJob(JobCondition.Succeeded, true, true);
                    return;
                }
                int maxStackSpace = actor.carryTracker.MaxStackSpaceEver(products[0].def);
                if (maxStackSpace < products[0].stackCount)
                {
                    int extraProduct = products[0].stackCount - maxStackSpace;
                    Thing thing3 = products[0].SplitOff(extraProduct);
                    if (!GenPlace.TryPlaceThing(thing3, actor.Position, actor.Map, ThingPlaceMode.Near, null, null, null, 1))
                    {
                        Log.Error(string.Format("{0} could not drop recipe extra product that pawn couldn't carry, {1} near {2}", actor, thing3, actor.Position));
                    }
                }
                if (maxStackSpace == 0)
                {
                    actor.jobs.EndCurrentJob(JobCondition.Succeeded, true, true);
                    return;
                }

                if (!actor.carryTracker.TryStartCarry(products[0]))
                {
                    Log.Error("Couldn't carry items!");
                }
                Pawn_JobTracker jobs = actor.jobs;
                Job newJob = HaulAIUtility.HaulToCellStorageJob(actor, products[0], invalid, false);
                JobCondition lastJobEndCondition = JobCondition.Succeeded;
                ThinkNode jobGiver = null;
                bool resumeCurJobAfterwards = false;
                bool cancelBusyStances = true;
                ThinkTreeDef thinkTree = null;
                bool? keepCarryingThingOverride = new bool?(true);
                jobs.StartJob(newJob, lastJobEndCondition, jobGiver, resumeCurJobAfterwards, cancelBusyStances, thinkTree, null, false, false, keepCarryingThingOverride, false, true, false);
            };

			__result = toil;
			return false;
        }

        private static List<Thing> GetProducts(Pawn actor, Job curJob, JobDriver_DoBill jobDriver_DoBill, List<Thing> ingredients, Thing dominantIngredient, ThingStyleDef style)
        {
            if (curJob.bill is Bill_Mech bill) return GenRecipe.FinalizeGestatedPawns(bill, actor, style).ToList();
            return GenRecipe.MakeRecipeProducts(curJob.RecipeDef, actor, ingredients, dominantIngredient, jobDriver_DoBill.BillGiver, curJob.bill.precept, style, curJob.bill.graphicIndexOverride).ToList();
        }

        private static ThingStyleDef SetStyle(Job curJob)
        {
            if (!ModsConfig.IdeologyActive || curJob.bill.recipe.products == null || curJob.bill.recipe.products.Count != 1) return null;
            ThingStyleDef thingStyleDef;
            if (!curJob.bill.globalStyle)
            {
                thingStyleDef = curJob.bill.style;
            }
            else
            {
                StyleCategoryPair styleCategoryPair = Faction.OfPlayer.ideos.PrimaryIdeo.style.StyleForThingDef(curJob.bill.recipe.ProducedThingDef, null);
                thingStyleDef = (styleCategoryPair?.styleDef);
            }
            return thingStyleDef;
        }

        private static void DoStockpileModes(Pawn actor, Job curJob, List<Thing> products, ref IntVec3 position)
        {
            if (curJob.bill.GetStoreMode() == BillStoreModeDefOf.BestStockpile)
            {
                StoreUtility.TryFindBestBetterStoreCellFor(products[0], actor, actor.Map, StoragePriority.Unstored, actor.Faction, out position);
            }
            else if (curJob.bill.GetStoreMode() == BillStoreModeDefOf.SpecificStockpile)
            {
                StoreUtility.TryFindBestBetterStoreCellForIn(products[0], actor, actor.Map, StoragePriority.Unstored, actor.Faction, curJob.bill.GetSlotGroup(), out position);
            }
            else
            {
                Log.ErrorOnce("Unknown store mode", 9158246);
            }
        }

        private static void NotifyQuestManager(Pawn actor, List<Thing> products)
        {
            if (products.Any())
            {
                Find.QuestManager.Notify_ThingsProduced(actor, products);
            }
        }

        private static void MakeTale(Pawn actor, Job curJob, List<Thing> products)
        {
            if (curJob.bill.recipe.WorkAmountTotal(curJob.GetTarget(TargetIndex.B).Thing) >= 10000f && products.Count > 0)
            {
                TaleRecorder.RecordTale(TaleDefOf.CompletedLongCraftingProject, new object[]
                {
                        actor,
                        products[0].GetInnerIfMinified().def
                });
            }
        }

        private static void TryPlaceThings(Pawn actor, List<Thing> products, Action<Thing, int> placedAction = null, int startAtThingNumber = 0)
        {
            if (startAtThingNumber > products.Count) return;

            for (int i = startAtThingNumber; i < products.Count; i++)
            {
                if (!GenPlace.TryPlaceThing(products[i], actor.Position, actor.Map, ThingPlaceMode.Near, placedAction))
                {
                    Log.Error(string.Concat(new object[]
                    {
                        actor,
                        " could not drop recipe product ",
                        products[i],
                        " near ",
                        actor.Position
                    }));
                }
            }
        }

        private static void EarnSkillFromJob(Pawn actor, Job curJob, JobDriver_DoBill jobDriver_DoBill)
        {
            if (curJob.RecipeDef.workSkill == null || curJob.RecipeDef.UsesUnfinishedThing || actor.skills == null) return;

            float xp = jobDriver_DoBill.ticksSpentDoingRecipeWork * 0.1f * curJob.RecipeDef.workSkillLearnFactor;
            actor.skills.GetSkill(curJob.RecipeDef.workSkill).Learn(xp, false);
        }
    }
}
