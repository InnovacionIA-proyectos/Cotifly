using Cotifly.Dominio.Agente;
using Cotifly.Dominio.Clientes;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Identidad;
using Cotifly.Dominio.Margen;
using Cotifly.Infraestructura.Comun.Persistence;
using Cotifly.Pruebas.Comun;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Cotifly.Infraestructura.Tests.Persistencia;

/// <summary>Piezas de persistencia que no necesitan base de datos.</summary>
public sealed class clsPersistenciaUnitariaTests
{
    [Theory]
    [InlineData("Violation of UNIQUE KEY constraint 'uq_cotizaciones_folio'. Cannot insert duplicate key in object 'dbo_cotifly.Cotizaciones'. The duplicate key value is (PCN-1).", "uq_cotizaciones_folio")]
    [InlineData("Cannot insert duplicate key row in object 'dbo_cotifly.UsuariosWeb' with unique index 'uq_usuariosWeb_nombreUsuario'. The duplicate key value is (ana).", "uq_usuariosWeb_nombreUsuario")]
    [InlineData("Infracción de la restricción UNIQUE KEY 'uq_cotizaciones_folio'. No se puede insertar una clave duplicada en el objeto 'dbo_cotifly.Cotizaciones'.", "uq_cotizaciones_folio")]
    [InlineData("Mensaje sin nombre de restricción", null)]
    public void ExtraerRestriccionEncuentraElNombreEnElMensajeDeSqlServer(string mensaje, string? esperado)
    {
        clsUnitOfWork.ExtraerRestriccion(mensaje).Should().Be(esperado);
    }

    [Fact]
    public void ElConvertidorDeEnumsUsaLosCodigosDelEsquema()
    {
        var convertidor = new clsConvertidorCodigoEnum<RolInterno>();

        convertidor.ConvertToProvider(RolInterno.GerenteComercial).Should().Be("gerente_comercial");
        convertidor.ConvertFromProvider("comercial").Should().Be(RolInterno.Comercial);
    }

    [Fact]
    public void ElEstadoDeConversacionSeSerializaEnCamelCaseConCodigosYSinNulos()
    {
        var estado = clsEstadoConversacion.Vacio with
        {
            NitCliente = "900123456",
            OrigenCliente = OrigenCliente.Prospecto,
            OrigenMargen = OrigenMargen.SapTm,
            CandidatosCliente = [new clsCandidatoCliente("800", "Otro", OrigenCliente.Cliente)],
        };

        var json = clsConvertidorEstadoConversacion.Serializar(estado);

        json.Should().Contain("\"nitCliente\":\"900123456\"")
            .And.Contain("\"origenCliente\":\"prospecto\"")
            .And.Contain("\"origenMargen\":\"sap_tm\"")
            .And.NotContain("destino");
        clsConvertidorEstadoConversacion.Deserializar(json).Should().Be(estado);
    }

    [Fact]
    public void ElModeloUsaElEsquemaDboCotiflyYLasTablasEnPascalCase()
    {
        using var proveedor = clsServiciosPruebas.Crear(null, new clsRelojFalso());
        using var ambito = proveedor.CreateScope();
        var modelo = ambito.ServiceProvider.GetRequiredService<clsCotiflyDbContext>().Model;

        modelo.GetDefaultSchema().Should().Be("dbo_cotifly");
        modelo.FindEntityType(typeof(clsCotizacion))!.GetTableName().Should().Be("Cotizaciones");
        modelo.FindEntityType(typeof(clsMargenPolitica))!.GetTableName().Should().Be("MargenesPolitica");
        modelo.GetEntityTypes().Should().HaveCount(15, "14 tablas del 0001-0002 y CotizacionPosiciones del 0004");
    }

    [Fact]
    public void ElContextoUsaNivelDeCompatibilidadDeSqlServer2019()
    {
        using var proveedor = clsServiciosPruebas.Crear(null, new clsRelojFalso());
        using var ambito = proveedor.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<clsCotiflyDbContext>();

        // EF1001: API interna de EF, aceptable solo en esta prueba para leer la opción configurada.
#pragma warning disable EF1001
        var extension = contexto.GetService<IDbContextOptions>()
            .FindExtension<Microsoft.EntityFrameworkCore.SqlServer.Infrastructure.Internal.SqlServerOptionsExtension>();

        extension!.SqlServerCompatibilityLevel.Should().Be(150);
#pragma warning restore EF1001
    }
}
