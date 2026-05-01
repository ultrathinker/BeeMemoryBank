using Android.App;

// All permissions must be declared via [assembly: UsesPermission] attributes.
// .NET 10 MAUI does NOT merge <uses-permission> from Platforms/Android/AndroidManifest.xml.
[assembly: UsesPermission(Android.Manifest.Permission.Internet)]
[assembly: UsesPermission(Android.Manifest.Permission.AccessNetworkState)]
[assembly: UsesPermission(Android.Manifest.Permission.ForegroundService)]
[assembly: UsesPermission("android.permission.FOREGROUND_SERVICE_DATA_SYNC")]
[assembly: UsesPermission(Android.Manifest.Permission.ReceiveBootCompleted)]
[assembly: UsesPermission("android.permission.POST_NOTIFICATIONS")]
[assembly: UsesPermission("android.permission.REQUEST_IGNORE_BATTERY_OPTIMIZATIONS")]
