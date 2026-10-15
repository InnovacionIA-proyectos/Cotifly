using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Infraestructura.Comun.Persistence;
using Cotifly.Pruebas.Comun;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Cotifly.Infraestructura.Tests.Persistencia;

/// <summary>
/// HU-023 (#176): el quinto motivo <c>BUSQUEDA_AGOTADA</c> y el script <c>db/scripts/07_motivo_busqueda_agotada.sql</c>, en bases aisladas
/// (Testcontainers). Con el 07 se guarda el motivo real; sin él, el backend lo detecta en <c>sys.check_constraints</c> y guarda NULL con
/// un aviso visible en la ruta. El CHECK del 07 rechaza un motivo con resultado <c>ASIGNADO</c> o NULL, se puede correr dos veces y el 03
/// pendiente no lo pisa.
/// </summary>
[Collection(clsColeccionSqlServer.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsMotivoBusquedaAgotadaTests(clsBaseDatosPruebas baseDatos) : clsPruebaIntegracion(baseDatos)
{
    private const string Script07 = clsBaseDatosPruebas.Script07;
    private const string Script03 = "../scripts_bd/pendientes/03_orden_vehiculo_y_conexo_catalogo.sql";

    [Fact]
    public async Task ConEl07SeGuardaBusquedaAgotada()
    {
        // La base principal de las pruebas tiene 0001-0010 + 02 + 04 + 07.
        await using var proveedor = clsServiciosPruebas.Crear(BaseDatos.CadenaCotifly, Reloj, BaseDatos.CadenaIdentidad);
        var (folio, rutaId) = await GuardarRutaSinSeleccionAsync(proveedor);

        (await clsBaseDatosPruebas.EscalarAsync<string>(BaseDatos.CadenaCotifly,
            $"SELECT CONCAT(resultadoCubicacion, '|', motivoSinSeleccion) FROM dbo_cotifly.CotizacionLineasSolicitud WHERE id = '{rutaId}'"))
            .Should().Be("SIN_SELECCION_AUTOMATICA|BUSQUEDA_AGOTADA", $"la cotización {folio} guarda el motivo real con el 07");
    }

    [Fact]
    public async Task SinEl07SeGuardaNullConElMotivoRealComoAvisoDeLaRuta()
    {
        await using var sin07 = await BaseDatos.CrearBaseConScriptsAsync("dbSin07", clsBaseDatosPruebas.Scripts.Length - 1);
        await using var proveedor = clsServiciosPruebas.Crear(sin07.Cadena, Reloj, BaseDatos.CadenaIdentidad);

        var (folio, rutaId) = await GuardarRutaSinSeleccionAsync(proveedor);

        (await clsBaseDatosPruebas.EscalarAsync<string>(sin07.Cadena,
            $"SELECT CONCAT(resultadoCubicacion, '|', ISNULL(motivoSinSeleccion, 'NULL')) FROM dbo_cotifly.CotizacionLineasSolicitud WHERE id = '{rutaId}'"))
            .Should().Be("SIN_SELECCION_AUTOMATICA|NULL", "sin el 07 el CHECK no admite BUSQUEDA_AGOTADA: respaldo de la opción A");
        await using var ambito = proveedor.CreateAsyncScope();
        var leida = await ambito.ServiceProvider.GetRequiredService<IClsCotizacionRepository>().ObtenerPorFolioAsync(folio, Ct);
        var ruta = leida!.RutasOrdenadas.Single();
        ruta.MotivoSinSeleccion.Should().BeNull();
        ruta.AvisosCubicacion!.Advertencias.Should().ContainSingle(a => a.Codigo == clsLineaSolicitud.CodigoAvisoMotivoNoGuardado)
            .Which.Mensaje.Should().StartWith("BUSQUEDA_AGOTADA:").And.NotContain("no cabe");
    }

    [Fact]
    public async Task SinEl07LosCuatroMotivosDelScript02SeGuardanTalCual()
    {
        await using var sin07 = await BaseDatos.CrearBaseConScriptsAsync("dbSin07b", clsBaseDatosPruebas.Scripts.Length - 1);
        await using var proveedor = clsServiciosPruebas.Crear(sin07.Cadena, Reloj, BaseDatos.CadenaIdentidad);

        var (_, rutaId) = await GuardarRutaSinSeleccionAsync(proveedor, "SUPERA_K_MAX");

        (await clsBaseDatosPruebas.EscalarAsync<string>(sin07.Cadena,
            $"SELECT motivoSinSeleccion FROM dbo_cotifly.CotizacionLineasSolicitud WHERE id = '{rutaId}'")).Should().Be("SUPERA_K_MAX");
    }

    [Fact]
    public async Task El07RechazaUnMotivoConResultadoAsignadoONull()
    {
        await using var con07 = await BaseDatos.CrearBaseConScriptsAsync("dbCheck07", clsBaseDatosPruebas.Scripts.Length);
        await using var proveedor = clsServiciosPruebas.Crear(con07.Cadena, Reloj, BaseDatos.CadenaIdentidad);
        var (folio, rutaId) = await GuardarRutaSinSeleccionAsync(proveedor);

        foreach (var resultado in new[] { "'ASIGNADO'", "NULL" })
        {
            var actualizar = () => clsBaseDatosPruebas.EjecutarAsync(con07.Cadena,
                $"UPDATE dbo_cotifly.CotizacionLineasSolicitud SET resultadoCubicacion = {resultado} WHERE id = '{rutaId}'");

            (await actualizar.Should().ThrowAsync<SqlException>($"motivo con resultado {resultado} en {folio}"))
                .Which.Message.Should().Contain("ck_cotizacionLineasSolicitud_motivoSinSeleccion");
        }
    }

    [Fact]
    public async Task El07CorridoDosVecesNoCambiaNadaYEl03DespuesNoLoPisa()
    {
        await using var baseAislada = await BaseDatos.CrearBaseConScriptsAsync("dbIdem07", clsBaseDatosPruebas.Scripts.Length - 1);
        var cadena = baseAislada.Cadena;
        const string Definicion = "SELECT definition FROM sys.check_constraints WHERE name = N'ck_cotizacionLineasSolicitud_motivoSinSeleccion'";
        (await clsBaseDatosPruebas.EscalarAsync<string>(cadena, Definicion)).Should().NotContain("BUSQUEDA_AGOTADA", "sin el 07");

        await clsBaseDatosPruebas.AplicarScriptAsync(cadena, Script07);
        var primera = await clsBaseDatosPruebas.EscalarAsync<string>(cadena, Definicion);
        var objeto = await clsBaseDatosPruebas.EscalarAsync<int>(cadena,
            "SELECT object_id FROM sys.check_constraints WHERE name = N'ck_cotizacionLineasSolicitud_motivoSinSeleccion'");
        await clsBaseDatosPruebas.AplicarScriptAsync(cadena, Script07);

        primera.Should().Contain("BUSQUEDA_AGOTADA").And.Contain("isnull");
        (await clsBaseDatosPruebas.EscalarAsync<string>(cadena, Definicion)).Should().Be(primera);
        (await clsBaseDatosPruebas.EscalarAsync<int>(cadena,
            "SELECT object_id FROM sys.check_constraints WHERE name = N'ck_cotizacionLineasSolicitud_motivoSinSeleccion'"))
            .Should().Be(objeto, "la segunda corrida no hace DROP ni ADD");
        (await clsBaseDatosPruebas.EscalarAsync<int>(cadena,
            "SELECT CAST(is_not_trusted AS INT) FROM sys.check_constraints WHERE name = N'ck_cotizacionLineasSolicitud_motivoSinSeleccion'"))
            .Should().Be(0, "WITH CHECK");

        await clsBaseDatosPruebas.AplicarScriptAsync(cadena, Script03);

        (await clsBaseDatosPruebas.EscalarAsync<string>(cadena, Definicion)).Should().Be(primera, "orden 07 → 03: el 03 no quita BUSQUEDA_AGOTADA");
    }

    [Fact]
    public async Task El03AntesYEl07DespuesTambienDejaLosCincoMotivos()
    {
        await using var baseAislada = await BaseDatos.CrearBaseConScriptsAsync("dbOrden0307", clsBaseDatosPruebas.Scripts.Length - 1);

        await clsBaseDatosPruebas.AplicarScriptAsync(baseAislada.Cadena, Script03);
        await clsBaseDatosPruebas.AplicarScriptAsync(baseAislada.Cadena, Script07);

        (await clsBaseDatosPruebas.EscalarAsync<string>(baseAislada.Cadena,
            "SELECT definition FROM sys.check_constraints WHERE name = N'ck_cotizacionLineasSolicitud_motivoSinSeleccion'"))
            .Should().Contain("BUSQUEDA_AGOTADA").And.Contain("isnull");
    }

    [Fact]
    public async Task LaSondaDeLaBaseSigueSanaSinEl07ConUnAvisoInformativo()
    {
        await using var sin07 = await BaseDatos.CrearBaseConScriptsAsync("dbSonda07", clsBaseDatosPruebas.Scripts.Length - 1);
        await using var proveedor = clsServiciosPruebas.Crear(sin07.Cadena, Reloj, BaseDatos.CadenaIdentidad);
        await using var ambito = proveedor.CreateAsyncScope();
        var sonda = ActivatorUtilities.CreateInstance<clsBaseDatosHealthCheck>(ambito.ServiceProvider);

        var resultado = await sonda.CheckHealthAsync(new HealthCheckContext(), Ct);

        resultado.Status.Should().Be(HealthStatus.Healthy, "el 07 no es obligatorio");
        resultado.Description.Should().Be(clsBaseDatosHealthCheck.DescripcionSin07);
    }

    /// <summary>Guarda una cotización con una ruta <c>SIN_SELECCION_AUTOMATICA</c> y el motivo dado; devuelve el folio y el id de la ruta.</summary>
    private static async Task<(string Folio, Guid RutaId)> GuardarRutaSinSeleccionAsync(ServiceProvider proveedor, string motivo = "BUSQUEDA_AGOTADA")
    {
        var folio = FolioUnico();
        var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = folio }).Valor;
        cotizacion.RegistrarCaptura(new clsEncabezadoSolicitudCosteo { Tipo = TipoSolicitud.Spot },
        [
            new clsRutaCapturada(clsSolicitudCosteoMuestra.RutaCaptura(), 1, 0, [new clsCamionPlaneado(null, null, 10_000m)], [],
                Cubicacion: new clsCubicacionRuta(Guid.NewGuid(), "SIN_SELECCION_AUTOMATICA", motivo, false)),
        ]);
        await using (var ambito = proveedor.CreateAsyncScope())
        {
            ambito.ServiceProvider.GetRequiredService<IClsCotizacionRepository>().Agregar(cotizacion);
            await ambito.ServiceProvider.GetRequiredService<IClsUnitOfWork>().GuardarCambiosAsync(Ct);
        }

        return (folio, cotizacion.RutasOrdenadas.Single().Id);
    }
}
