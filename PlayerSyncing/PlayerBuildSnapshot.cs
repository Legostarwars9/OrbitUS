using UnityEngine;

namespace Orbit_Us
{
    public sealed class PlayerBuildSnapshot
    {
        public static readonly string[] PlayerFields={"hpMax","fuelCap","boostCap","boostRechargeSpeed","fuelMiningSpeed","solarPanels","fuelInsuranceLevel","solarSailLevel"};
        public static readonly string[] WeaponFields={"loadedProj","modificationsOne","modificationsTwo","modificationsInventory","projectilesInventory","subProjectiles1","subProjectiles2","whatWeapon","boostMax","boostRechargeSpeed","heatLossSpeed","subSlotCount","standardDelay","recoilForce"};
        public static readonly string[] MovementFields={"speed","turnSpeed","turnCap","dilMultiplier"};
        private BuildFieldSnapshot playerBuild,weapons,movementBuild;
        private int money;
        private float mass;
        private Vector3 scale;
        private shooterWithLoadout shooter;
        private movement movement;

        public static PlayerBuildSnapshot Capture(player local)
        {
            var saved=new PlayerBuildSnapshot {playerBuild=BuildFieldSnapshot.Capture(local,PlayerFields),money=local.scraps,
                mass=local.rb.mass,scale=local.transform.localScale,shooter=local.shooter,movement=local.GetComponent<movement>()};
            if(saved.shooter!=null)saved.weapons=BuildFieldSnapshot.Capture(saved.shooter,WeaponFields);
            if(saved.movement!=null)saved.movementBuild=BuildFieldSnapshot.Capture(saved.movement,MovementFields);
            return saved;
        }
        public void Restore(player local)
        {
            int currentMoney=local.scraps;
            playerBuild.Restore(local);local.scraps=BuildFieldSnapshot.RetainedMoney(money,currentMoney);
            local.rb.mass=mass;local.transform.localScale=scale;
            if(shooter!=null)
            {
                weapons.Restore(shooter);shooter.grappleOut=false;shooter.inventoryOpen=false;shooter.boostDuration=shooter.boostMax;
                foreach(string name in new[]{"heat","shootDelay"})
                {var array=BuildFieldSnapshot.Field(typeof(shooterWithLoadout),name).GetValue(shooter) as float[];if(array!=null)System.Array.Clear(array,0,array.Length);}
                var overheated=BuildFieldSnapshot.Field(typeof(shooterWithLoadout),"overheated").GetValue(shooter) as bool[];
                if(overheated!=null)System.Array.Clear(overheated,0,overheated.Length);
                shooter.OpenSubSlots();
            }
            if(movement!=null)movementBuild.Restore(movement);
            local.hp=local.hpMax;local.fuel=local.fuelCap;local.boostDuration=local.boostCap;
            local.stunTime=0;
            BuildFieldSnapshot.Field(typeof(player),"hurtTimer").SetValue(local,0f);
            var hits=BuildFieldSnapshot.Field(typeof(health),"deadProjectiles").GetValue(local) as System.Collections.IList;
            if(hits!=null)hits.Clear();
            if(local.temperature!=null)
            {local.temperature.heat=0;local.temperature.lastHeat=0;local.temperature.heatRate=0;local.temperature.disintegrating=false;local.temperature.mergeMomentum=0;}
        }
    }
}
