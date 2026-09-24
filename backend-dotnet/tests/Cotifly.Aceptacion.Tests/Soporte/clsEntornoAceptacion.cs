using Cotifly.Pruebas.Comun;
using Reqnroll;

// Todas las pruebas de aceptación usan SQL Server en Testcontainers: se excluyen con
// --filter-not-trait "Categoria=Integracion" igual que las de integración.
[assembly: Trait("Categoria", "Integracion")]

namespace Cotifly.Aceptacion.Tests.Soporte;

/// <summary>
/// Un SQL Server 2019 (Testcontainers) para toda la corrida de aceptación, con el esquema de
/// <c>migrations_mssql/0001</c> y <c>0002</c> aplicado (ver <see cref="clsBaseDatosPruebas"/>).
/// </summary>
[Binding]
public sealed class clsEntornoAceptacion
{
    private static clsBaseDatosPruebas? _baseDatos;

    public static clsBaseDatosPruebas BaseDatos =>
        _baseDatos ?? throw new InvalidOperationException("La base de pruebas no se inició (BeforeTestRun).");

    [BeforeTestRun]
    public static async Task IniciarAsync()
    {
        _baseDatos = new clsBaseDatosPruebas();
        await _baseDatos.InitializeAsync();
    }

    [AfterTestRun]
    public static async Task DetenerAsync()
    {
        if (_baseDatos is not null)
        {
            await _baseDatos.DisposeAsync();
        }
    }
}
