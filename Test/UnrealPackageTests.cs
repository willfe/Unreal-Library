using System.IO;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using UELib.Core;

namespace UELib.Test
{
    [TestClass]
    public class UnrealPackageTests
    {
        [TestMethod]
        public void LoadPackageTest()
        {
            string testPackagePath = Path.Join(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "upk", "TestUC2", "TestUC2.u");
            var package = UnrealLoader.LoadPackage(testPackagePath);
            Assert.IsNotNull(package);

            // FIXME: UELib is dependent on WinForms
            // (No longer an issue with Net 5.0, but we should still refactor the library to be independant)
            package.InitializePackage();

            var testClass = package.FindObject("Test", typeof(UClass));
            Assert.IsNotNull(testClass);

            string testClassContent = testClass.Decompile();
            Assert.AreNotSame("", testClassContent);
        }
    }
}
