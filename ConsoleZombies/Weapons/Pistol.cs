﻿using System;

namespace ConsoleZombies
{
    public class Pistol : Weapon
    {
        public override void Fire()
        {
            var angle = MainCharacter.Current.Target != null ?
                MainCharacter.Current.Bounds.Location.CalculateAngleTo(MainCharacter.Current.Target.Bounds.Location) :
                MainCharacter.Current.Speed.Angle;

            var bullet = new Bullet(MainCharacter.Current.Bounds.Location,angle);
            bullet.Speed.HitDetectionTypes.Remove(typeof(MainCharacter));
            MainCharacter.Current.Realm.Add(bullet);
        }
    }
}